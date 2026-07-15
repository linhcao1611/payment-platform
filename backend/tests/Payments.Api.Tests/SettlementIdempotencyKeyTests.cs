using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Payments.Infrastructure.Gateway;

namespace Payments.Api.Tests;

/// <summary>
/// Pins the *worker's* choice of gateway idempotency key, which the gateway's own tests can't
/// see. The key only protects anything if it is stable across attempts — a fresh one per call
/// makes every retry a new settlement, which is exactly the bug the key exists to prevent, and
/// it would leave every other test green.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class SettlementIdempotencyKeyTests(PaymentsApiFixture fixture)
{
    /// <summary>Approves authorizations, fails every settlement, and records the keys it saw.</summary>
    private sealed class RecordingGateway : IPaymentGateway
    {
        public ConcurrentQueue<string> SettleKeys { get; } = new();

        public Task<GatewayResult> AuthorizeAsync(
            string cardToken, long amountMinor, string currency, CancellationToken ct) =>
            Task.FromResult(new GatewayResult(true, null));

        public Task<GatewayResult> SettleAsync(Guid paymentId, string idempotencyKey, CancellationToken ct)
        {
            SettleKeys.Enqueue(idempotencyKey);

            // Always transient-fail, to force the worker to retry the same job several times.
            return Task.FromResult(new GatewayResult(false, "acquirer timeout (forced)"));
        }
    }

    /// <summary>Own database, own worker, own gateway — nothing else claims these jobs.</summary>
    private async Task<WebApplicationFactory<Program>> HostWith(RecordingGateway gateway) =>
        new CustomHost(await fixture.CreateIsolatedDatabaseAsync(), gateway);

    private sealed class CustomHost(string connectionString, RecordingGateway gateway)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);

            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Payments"] = connectionString,
                    ["Settlement:Enabled"] = "true",
                    ["Settlement:PollInterval"] = "00:00:00.100",
                    ["Settlement:BaseDelay"] = "00:00:00.100",
                    ["Settlement:MaxDelay"] = "00:00:00.200",
                    ["Settlement:MaxAttempts"] = "3",
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPaymentGateway>();
                services.AddSingleton<IPaymentGateway>(gateway);
            });
        }
    }

    [Fact]
    public async Task Every_retry_of_one_job_reuses_the_same_gateway_idempotency_key()
    {
        var gateway = new RecordingGateway();
        await using var host = await HostWith(gateway);
        var api = PaymentsApiClient.For(host.CreateClient(), $"m-{Guid.NewGuid():N}");

        var payment = await api.CreateAuthorized();
        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        // Wait for the worker to burn through its attempts on this one job.
        var deadline = Stopwatch.StartNew();
        while (gateway.SettleKeys.Count < 3 && deadline.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(100);

        Assert.True(gateway.SettleKeys.Count >= 2, $"expected repeated attempts, saw {gateway.SettleKeys.Count}");

        // The load-bearing assertion: N attempts at the acquirer, one key. If the worker minted
        // a fresh key per attempt, a settlement that actually succeeded but timed out would be
        // retried as a *second* settlement — the acquirer would move the money twice.
        var distinct = gateway.SettleKeys.Distinct().ToList();
        Assert.Single(distinct);

        // And it's the job's identity that's stable, not a per-payment or per-process constant.
        Assert.True(Guid.TryParse(distinct[0], out _), $"expected the job id as the key, got '{distinct[0]}'");
        Assert.NotEqual(payment.Id.ToString(), distinct[0]);
    }
}
