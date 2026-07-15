using System.Net;

namespace Payments.Api.Tests;

/// <summary>
/// Idempotency is the reason this system can be retried safely, and none of it can be proven
/// without a real database: the replay path, the conflict and the concurrent race are all
/// decided by a unique index and a transaction.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class IdempotencyTests(PaymentsApiFixture fixture)
{
    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");

    [Fact]
    public async Task Create_without_an_idempotency_key_is_rejected()
    {
        var api = NewMerchant();

        var response = await api.CreateRaw(PaymentsApiClient.NewPaymentBody(), idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task Replaying_a_create_returns_the_identical_response_and_creates_nothing_new()
    {
        var api = NewMerchant();
        var key = Guid.NewGuid().ToString();
        var body = PaymentsApiClient.NewPaymentBody(amountMinor: 4200, description: "replay me");

        var first = await api.CreateRaw(body, key);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await api.CreateRaw(body, key);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Byte-identical, not merely equivalent — the stored response is replayed verbatim.
        Assert.Equal(firstBody, secondBody);

        // And the caller can tell it was a replay rather than a second charge.
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));

        var listed = await api.Http.GetStringAsync("/api/payments");
        Assert.Contains("\"totalCount\":1", listed);
    }

    [Fact]
    public async Task A_reused_key_with_a_different_payload_is_a_conflict()
    {
        var api = NewMerchant();
        var key = Guid.NewGuid().ToString();

        await api.CreateRaw(PaymentsApiClient.NewPaymentBody(amountMinor: 4200), key);
        var response = await api.CreateRaw(PaymentsApiClient.NewPaymentBody(amountMinor: 9999), key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_conflict", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task Reordering_the_json_of_an_identical_request_still_replays()
    {
        var api = NewMerchant();
        var key = Guid.NewGuid().ToString();

        var first = await api.CreateRaw(
            new { amountMinor = 4200L, currency = "USD", cardToken = "tok_visa", cardLast4 = "4242", cardBrand = "visa", description = "order" },
            key);

        // Same request, different property order on the wire. The fingerprint hashes the parsed
        // DTO, so this must be a replay — not a conflict.
        var second = await api.CreateRaw(
            new { description = "order", cardBrand = "visa", cardLast4 = "4242", cardToken = "tok_visa", currency = "USD", amountMinor = 4200L },
            key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_key_cannot_replay_across_two_different_payments()
    {
        var api = NewMerchant();
        var a = await api.CreateAuthorized();
        var b = await api.CreateAuthorized();
        var key = Guid.NewGuid().ToString();

        var capturedA = await api.CaptureRaw(a.Id, key);
        var capturedB = await api.CaptureRaw(b.Id, key);

        Assert.Equal(HttpStatusCode.OK, capturedA.StatusCode);

        // The payment id is part of the fingerprint, so reusing the key on another payment is a
        // conflict rather than silently replaying A's response for B — and B stays Authorized.
        Assert.Equal(HttpStatusCode.Conflict, capturedB.StatusCode);
        Assert.Equal("idempotency_conflict", (await PaymentsApiClient.Problem(capturedB)).ErrorCode);
        Assert.Equal("Authorized", (await api.Get(b.Id)).Status);
    }

    [Fact]
    public async Task An_idempotency_key_is_scoped_to_one_merchant()
    {
        var alice = NewMerchant();
        var bob = NewMerchant();
        var key = Guid.NewGuid().ToString();
        var body = PaymentsApiClient.NewPaymentBody(amountMinor: 4200);

        var aliceCreated = await alice.CreateRaw(body, key);
        var bobCreated = await bob.CreateRaw(body, key);

        // Same key, same payload, different tenants: Bob must get his own payment, not a replay
        // of Alice's. Keys are namespaced by merchant, so clients can't collide with each other.
        Assert.Equal(HttpStatusCode.Created, aliceCreated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobCreated.StatusCode);
        Assert.False(bobCreated.Headers.Contains("Idempotency-Replayed"));
        Assert.NotEqual((await alice.Read(aliceCreated)).Id, (await bob.Read(bobCreated)).Id);
    }

    [Fact]
    public async Task Concurrent_creates_with_one_key_produce_exactly_one_payment()
    {
        var api = NewMerchant();
        var key = Guid.NewGuid().ToString();
        var body = PaymentsApiClient.NewPaymentBody(amountMinor: 4200);

        // The scenario this whole design exists for: a client's retry racing its own original
        // request. Serialised handling would be an easy pass, so fire them together.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => api.CreateRaw(body, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var ids = new List<Guid>();
        foreach (var response in responses)
            ids.Add((await api.Read(response)).Id);

        // One payment, and every racer was told about that same one.
        Assert.Single(ids.Distinct());

        var listed = await api.Http.GetStringAsync("/api/payments");
        Assert.Contains("\"totalCount\":1", listed);

        // Exactly one of them did the work; the rest replayed.
        Assert.Equal(1, responses.Count(r => !r.Headers.Contains("Idempotency-Replayed")));
    }

    [Fact]
    public async Task Concurrent_captures_with_one_key_enqueue_exactly_one_settlement_job()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => api.CaptureRaw(payment.Id, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, responses.Count(r => !r.Headers.Contains("Idempotency-Replayed")));

        // The capture transition is written once, so the outbox row is written once — a
        // duplicated job here would mean settling the same payment twice at the acquirer.
        var trail = await api.Transitions(payment.Id);
        Assert.Single(trail.Where(t => t.ToStatus == "Captured"));
    }
}
