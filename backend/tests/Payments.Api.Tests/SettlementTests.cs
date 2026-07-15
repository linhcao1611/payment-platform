using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Payments.Api.Tests;

/// <summary>
/// The async leg. Assertions here are about what the system converges on rather than what one
/// request returns.
///
/// Each test runs a worker on its own database. Sharing one would mean every other test in the
/// suite has a background thread quietly settling its payments, which turns any exact-audit-
/// trail assertion into a race against the machine's speed.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class SettlementTests(PaymentsApiFixture fixture)
{
    /// <summary>A host whose worker is the only thing polling its (private) queue.</summary>
    private async Task<WebApplicationFactory<Program>> WorkerHostAsync() =>
        fixture.CreateHost(await fixture.CreateIsolatedDatabaseAsync(), workerEnabled: true);

    private static PaymentsApiClient ClientFor(WebApplicationFactory<Program> host) =>
        PaymentsApiClient.For(host.CreateClient(), $"m-{Guid.NewGuid():N}");

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
        await using var host = await WorkerHostAsync();
        var api = ClientFor(host);
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
        await using var host = await WorkerHostAsync();
        var api = ClientFor(host);
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
        await using var host = await WorkerHostAsync();
        var api = ClientFor(host);
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
        // Two hosts, one private database, started in sequence: the capture and refund land
        // while nothing is polling, and only then does a worker get to look. Racing a running
        // worker would make this green on a warm machine and red on a cold one.
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        var merchantId = $"m-{Guid.NewGuid():N}";

        Guid paymentId;
        await using (var quiet = fixture.CreateHost(connectionString, workerEnabled: false))
        {
            var api = PaymentsApiClient.For(quiet.CreateClient(), merchantId);
            var payment = await api.CreateAuthorized();
            paymentId = payment.Id;

            await api.CaptureRaw(paymentId, Guid.NewGuid().ToString());
            await api.RefundRaw(paymentId, "changed mind", Guid.NewGuid().ToString());

            Assert.Equal("Refunded", (await api.Get(paymentId)).Status);
        }

        // Only now does a worker see the queue. The job is real and due, and its payment has
        // moved on — it must resolve it as moot rather than drive Refunded -> Settled and
        // retry itself to death against a transition that can never be legal.
        await using var worker = fixture.CreateHost(connectionString, workerEnabled: true);
        var observer = PaymentsApiClient.For(worker.CreateClient(), merchantId);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var trail = await observer.Transitions(paymentId);
        Assert.DoesNotContain(trail, t => t.ToStatus == "Settled");
        Assert.Equal(["Pending", "Authorized", "Captured", "Refunded"], trail.Select(t => t.ToStatus));
    }
}
