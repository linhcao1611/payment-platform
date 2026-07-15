using System.Diagnostics;
using System.Net;

namespace Payments.Api.Tests;

/// <summary>
/// The async leg. These use the host that has the settlement worker running, so the
/// assertions are about what the system converges on rather than what one request returns.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class SettlementTests(PaymentsApiFixture fixture)
{
    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.ApiWithWorker.CreateClient(), $"m-{Guid.NewGuid():N}");

    /// <summary>
    /// Polls instead of sleeping a fixed duration: a fixed sleep is either flaky or slow, and
    /// this asserts the outcome the moment it arrives.
    /// </summary>
    private static async Task<PaymentDto> WaitForStatus(
        PaymentsApiClient api, Guid id, string status, int timeoutSeconds = 20)
    {
        var deadline = Stopwatch.StartNew();
        PaymentDto payment;

        do
        {
            payment = await api.Get(id);
            if (payment.Status == status)
                return payment;

            await Task.Delay(100);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds));

        Assert.Fail($"Payment {id} was {payment.Status}, expected {status} within {timeoutSeconds}s.");
        throw new UnreachableException();
    }

    [Fact]
    public async Task A_captured_payment_settles_itself()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        // Nothing else is asked of the API — the worker picks the job up on its own.
        var settled = await WaitForStatus(api, payment.Id, "Settled");
        Assert.Equal("Settled", settled.Status);

        var trail = await api.Transitions(payment.Id);
        Assert.Equal(["Pending", "Authorized", "Captured", "Settled"], trail.Select(t => t.ToStatus));

        var captured = trail.Single(t => t.ToStatus == "Captured");
        var settledRow = trail.Single(t => t.ToStatus == "Settled");

        // The worker is a distinct actor in the audit trail — no merchant asked for this.
        Assert.Equal("settlement-worker", settledRow.Actor);
        Assert.StartsWith("merchant:", captured.Actor);

        // And the correlation id survives the async boundary: the settlement carries the id of
        // the capture request that caused it, which is what makes one payment greppable
        // end to end across the queue.
        Assert.Equal(captured.CorrelationId, settledRow.CorrelationId);
    }

    [Fact]
    public async Task The_capture_request_id_flows_onto_settlement_even_when_the_client_supplies_it()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        var correlationId = $"trace-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{payment.Id}/capture");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await api.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-Id").Single());

        await WaitForStatus(api, payment.Id, "Settled");

        // A caller's own trace id reaches the worker's audit row — this is the story an
        // operator actually uses when a merchant asks what happened to their payment.
        var trail = await api.Transitions(payment.Id);
        Assert.Equal(correlationId, trail.Single(t => t.ToStatus == "Settled").CorrelationId);
    }

    [Fact]
    public async Task Refund_after_settle_is_allowed()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();
        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());
        await WaitForStatus(api, payment.Id, "Settled");

        var refund = await api.RefundRaw(payment.Id, "post-settlement refund", Guid.NewGuid().ToString());

        // Settled -> Refunded is a legal edge: money already moved, so this is the real-world
        // refund rather than a cancellation.
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        Assert.Equal("Refunded", (await api.Get(payment.Id)).Status);
    }

    [Fact]
    public async Task A_payment_refunded_before_settlement_is_never_settled()
    {
        // Uses the worker-less host to win the race deterministically: capture and refund land
        // before any worker can claim the job.
        var setup = PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");
        var payment = await setup.CreateAuthorized();
        await setup.CaptureRaw(payment.Id, Guid.NewGuid().ToString());
        await setup.RefundRaw(payment.Id, "changed mind", Guid.NewGuid().ToString());

        Assert.Equal("Refunded", (await setup.Get(payment.Id)).Status);

        // The job is still queued and the other host's worker will pick it up. It must resolve
        // the job as moot rather than drive Refunded -> Settled and retry itself to death.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal("Refunded", (await setup.Get(payment.Id)).Status);

        var trail = await setup.Transitions(payment.Id);
        Assert.DoesNotContain(trail, t => t.ToStatus == "Settled");
        Assert.Equal(["Pending", "Authorized", "Captured", "Refunded"], trail.Select(t => t.ToStatus));
    }
}
