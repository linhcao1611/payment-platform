using Microsoft.Extensions.Options;
using Payments.Infrastructure.Gateway;

namespace Payments.Api.Tests;

/// <summary>
/// The gateway idempotency contract. The fake stands in for an acquirer, so these tests pin
/// the behaviour the worker is relying on — approvals alone can't tell a replay from a second
/// charge, so they assert how many times the acquirer was actually contacted.
/// </summary>
public class FakeGatewaySettlementTests
{
    private static (FakePaymentGateway Gateway, FakeGatewayOptions Options) NewGateway(
        double settleFailureRate = 0)
    {
        var options = new FakeGatewayOptions
        {
            SettleFailureRate = settleFailureRate,
            MinLatencyMs = 0,
            MaxLatencyMs = 1,
        };

        return (new FakePaymentGateway(Options.Create(options)), options);
    }

    [Fact]
    public async Task Retrying_a_settled_job_replays_instead_of_settling_again()
    {
        var (gateway, _) = NewGateway();
        var paymentId = Guid.NewGuid();
        var jobId = Guid.NewGuid().ToString();

        var first = await gateway.SettleAsync(paymentId, jobId, CancellationToken.None);
        var second = await gateway.SettleAsync(paymentId, jobId, CancellationToken.None);

        Assert.True(first.Approved);
        Assert.True(second.Approved);

        // The money moved once. This is the whole point: a redelivered job must not double-settle.
        Assert.Equal(1, gateway.AcquirerSettlementCalls);
    }

    [Fact]
    public async Task Concurrent_redeliveries_of_one_job_contact_the_acquirer_once()
    {
        var (gateway, _) = NewGateway();
        var paymentId = Guid.NewGuid();
        var jobId = Guid.NewGuid().ToString();

        // Two workers racing on a lease that lapsed mid-call — the scenario the key exists for.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => gateway.SettleAsync(paymentId, jobId, CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.Approved));
        Assert.Equal(1, gateway.AcquirerSettlementCalls);
    }

    [Fact]
    public async Task A_transient_failure_does_not_burn_the_key()
    {
        var (gateway, options) = NewGateway(settleFailureRate: 1.0);
        var paymentId = Guid.NewGuid();
        var jobId = Guid.NewGuid().ToString();

        var failed = await gateway.SettleAsync(paymentId, jobId, CancellationToken.None);
        Assert.False(failed.Approved);

        // The acquirer recovers. The worker retries the same job — and must actually reach it,
        // rather than replay the failure forever. Caching a transient failure under the key
        // would strand every payment behind one bad minute at the acquirer.
        options.SettleFailureRate = 0;
        var succeeded = await gateway.SettleAsync(paymentId, jobId, CancellationToken.None);

        Assert.True(succeeded.Approved);
        Assert.Equal(2, gateway.AcquirerSettlementCalls);
    }

    [Fact]
    public async Task Different_jobs_settle_independently()
    {
        var (gateway, _) = NewGateway();

        await gateway.SettleAsync(Guid.NewGuid(), Guid.NewGuid().ToString(), CancellationToken.None);
        await gateway.SettleAsync(Guid.NewGuid(), Guid.NewGuid().ToString(), CancellationToken.None);

        // Dedupe must be per key, not a global "already settled once" latch.
        Assert.Equal(2, gateway.AcquirerSettlementCalls);
    }
}
