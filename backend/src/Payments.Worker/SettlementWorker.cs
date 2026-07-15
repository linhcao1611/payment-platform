using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Entities;
using Payments.Infrastructure.Gateway;

namespace Payments.Worker;

/// <summary>
/// Drains the settlement queue: claims due jobs, asks the gateway to settle, and moves the
/// payment Captured → Settled. Retries with backoff and dead-letters after MaxAttempts.
///
/// Hosted in the API process today, but isolated in its own project so the split into a
/// separate deployment is a project reference away — nothing here touches HTTP.
/// </summary>
public sealed class SettlementWorker(
    IServiceScopeFactory scopes,
    IOptions<SettlementOptions> options,
    TimeProvider clock,
    ILogger<SettlementWorker> logger) : BackgroundService
{
    /// <summary>Audit actor for transitions this worker causes — no merchant is on the call stack.</summary>
    private const string Actor = "settlement-worker";

    private const int MaxErrorLength = 1024; // last_error column width

    private readonly SettlementOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Settlement worker is disabled by configuration; not polling.");
            return;
        }

        logger.LogInformation(
            "Settlement worker started (poll {PollInterval}, batch {BatchSize}, max {MaxAttempts} attempts)",
            _options.PollInterval, _options.BatchSize, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Never let a poll failure (DB blip, etc.) kill the loop — leased jobs are
                // reclaimed after their lease lapses, so backing off and retrying is safe.
                logger.LogError(e, "Settlement poll failed; retrying in {PollInterval}", _options.PollInterval);
            }

            // A full batch probably means more work is waiting — drain before sleeping.
            if (claimed == _options.BatchSize)
                continue;

            try
            {
                await Task.Delay(_options.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Settlement worker stopped.");
    }

    private async Task<int> PollOnceAsync(CancellationToken ct)
    {
        // A scope per poll, not per process: the DbContext is scoped, and holding one open
        // for the worker's lifetime would leak a connection and an ever-growing change tracker.
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ISettlementQueue>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();

        var jobs = await queue.ClaimDueAsync(_options.BatchSize, clock.GetUtcNow(), _options.LeaseDuration, ct);

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessAsync(job, queue, payments, gateway, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down mid-batch: the lease expires and another worker picks it up.
                throw;
            }
            catch (Exception e)
            {
                // One poison job must not strand the rest of the batch.
                logger.LogError(e, "Settlement job {SettlementJobId} threw; leaving it to its lease", job.Id);
            }
        }

        return jobs.Count;
    }

    private async Task ProcessAsync(
        SettlementJob job,
        ISettlementQueue queue,
        IPaymentRepository payments,
        IPaymentGateway gateway,
        CancellationToken ct)
    {
        // The correlation id was captured at capture time and carried on the job row. Pushing
        // it into the scope here is what makes the worker's logs join to the API request that
        // caused this settlement — the whole point of persisting it across the async boundary.
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = job.CorrelationId,
            ["PaymentId"] = job.PaymentId,
            ["SettlementJobId"] = job.Id,
            ["Attempt"] = job.AttemptCount,
        });

        var payment = await payments.GetForSettlementAsync(job.PaymentId, ct);
        if (payment is null)
        {
            // The FK makes this unreachable today; resolve rather than spin if it ever isn't.
            await ResolveAsync(job, SettlementJobStatus.Cancelled, "payment no longer exists", queue, ct);
            return;
        }

        // Delivery is at-least-once, and a payment can be refunded between capture and
        // settlement. Either way the job's work may already be moot, so check the aggregate
        // rather than driving it into an illegal transition and retrying until dead-lettered.
        if (payment.Status is not PaymentStatus.Captured)
        {
            var (status, reason) = payment.Status switch
            {
                // Redelivery after the payment was already settled: the job is the duplicate,
                // not an error. Converge on done.
                PaymentStatus.Settled => (SettlementJobStatus.Succeeded, "payment was already settled"),
                _ => (SettlementJobStatus.Cancelled, $"payment is {payment.Status}, no longer settleable"),
            };

            logger.LogInformation(
                "Settlement job resolved as {JobStatus} without contacting the gateway: {Reason}", status, reason);
            await ResolveAsync(job, status, reason, queue, ct);
            return;
        }

        GatewayResult result;
        try
        {
            // Deliberately outside a transaction: the claim already leased this job, so there
            // is nothing to protect and no reason to pin a connection across the acquirer call.
            result = await gateway.SettleAsync(job.PaymentId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await RetryOrDeadLetterAsync(job, e.Message, queue, ct);
            return;
        }

        if (!result.Approved)
        {
            await RetryOrDeadLetterAsync(job, result.DeclineReason ?? "settlement declined", queue, ct);
            return;
        }

        var now = clock.GetUtcNow();
        payment.MarkSettled(Actor, job.CorrelationId, now);
        job.Status = SettlementJobStatus.Succeeded;
        job.LastError = null;
        job.CompletedAt = now;

        // Saves the payment, its audit transition and the job's completion together: the job
        // cannot be marked done unless the payment really reached Settled.
        await payments.SaveAsync(payment, ct);

        logger.LogInformation("Payment settled on attempt {Attempt}", job.AttemptCount);
    }

    private async Task RetryOrDeadLetterAsync(
        SettlementJob job, string error, ISettlementQueue queue, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        job.LastError = Truncate(error);

        if (job.AttemptCount >= _options.MaxAttempts)
        {
            job.Status = SettlementJobStatus.Dead;
            job.CompletedAt = now;

            // The answer to "what happens when settlement fails five times at 2am": the payment
            // stays Captured, the row is queryable, and slice 6 scrapes a counter off it. Money
            // is never silently lost — it's parked where an operator can find it.
            logger.LogError(
                "Settlement job dead-lettered after {Attempt} attempts: {LastError}",
                job.AttemptCount, job.LastError);
        }
        else
        {
            var delay = SettlementBackoff.Compute(job.AttemptCount, _options, Random.Shared.NextDouble());
            job.Status = SettlementJobStatus.Pending;
            job.NextAttemptAt = now + delay;

            logger.LogWarning(
                "Settlement attempt {Attempt} failed ({LastError}); retrying in {Delay}",
                job.AttemptCount, job.LastError, delay);
        }

        await queue.SaveAsync(ct);
    }

    private async Task ResolveAsync(
        SettlementJob job, SettlementJobStatus status, string reason, ISettlementQueue queue, CancellationToken ct)
    {
        job.Status = status;
        job.LastError = Truncate(reason);
        job.CompletedAt = clock.GetUtcNow();
        await queue.SaveAsync(ct);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
