using System.Net;

namespace Payments.Api.Tests;

/// <summary>
/// The paths that would cost real money if they broke. Each test gets its own merchant id, so
/// the shared container needs no cleanup between tests and the merchant-scoping rule does the
/// isolation for us — which is itself worth exercising.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class PaymentLifecycleTests(PaymentsApiFixture fixture)
{
    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");

    [Fact]
    public async Task Create_authorizes_and_records_an_audit_trail()
    {
        var api = NewMerchant();

        var payment = await api.CreateAuthorized(amountMinor: 4200, description: "lifecycle");

        Assert.Equal("Authorized", payment.Status);
        Assert.Equal(4200, payment.AmountMinor);

        var trail = await api.Transitions(payment.Id);
        Assert.Collection(trail,
            t => Assert.Equal((null, "Pending"), (t.FromStatus, t.ToStatus)),
            t => Assert.Equal(("Pending", "Authorized"), (t.FromStatus, t.ToStatus)));
    }

    [Fact]
    public async Task A_declined_card_fails_the_payment_and_it_cannot_be_captured()
    {
        var api = NewMerchant();

        // The fake gateway's deterministic hook: this token always declines.
        var response = await api.CreateRaw(
            PaymentsApiClient.NewPaymentBody(cardToken: "tok_visa-declined"), Guid.NewGuid().ToString());

        // A declined authorization is a successful request with a Failed payment, not an error.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await api.Read(response);
        Assert.Equal("Failed", payment.Status);

        var capture = await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, capture.StatusCode);
        Assert.Equal("invalid_state_transition", (await PaymentsApiClient.Problem(capture)).ErrorCode);
    }

    [Fact]
    public async Task Capture_then_refund_walks_the_state_machine_and_the_trail_shows_it()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        var capture = await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);

        var refund = await api.RefundRaw(payment.Id, "customer request", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);

        Assert.Equal("Refunded", (await api.Get(payment.Id)).Status);

        var trail = await api.Transitions(payment.Id);
        Assert.Equal(
            ["Pending", "Authorized", "Captured", "Refunded"],
            trail.Select(t => t.ToStatus));

        // The reason a merchant gave is on the audit row, not just in a log line.
        Assert.Equal("customer request", trail[^1].Reason);
    }

    [Fact]
    public async Task Capturing_an_already_captured_payment_is_a_409()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        // A *new* key, so this is a genuinely new request rather than a replay — the state
        // machine has to reject it, not idempotency.
        var second = await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("invalid_state_transition", (await PaymentsApiClient.Problem(second)).ErrorCode);
    }

    [Fact]
    public async Task A_failed_capture_does_not_consume_its_idempotency_key()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();
        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        var key = Guid.NewGuid().ToString();
        var first = await api.CaptureRaw(payment.Id, key);
        var second = await api.CaptureRaw(payment.Id, key);

        // Both are the state-machine's 409. If the failure had been stored under the key, the
        // second call would replay it — same status here, so assert it is NOT a replay.
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task Payments_are_invisible_to_other_merchants()
    {
        var owner = NewMerchant();
        var stranger = NewMerchant();

        var payment = await owner.CreateAuthorized();

        var response = await stranger.GetRaw(payment.Id);

        // 404, not 403: a merchant should not learn that someone else's payment id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("payment_not_found", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }
}
