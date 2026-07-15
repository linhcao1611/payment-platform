using System.Net;
using System.Net.Http.Json;

namespace Payments.Api.Tests;

/// <summary>
/// Every string the API accepts ends up in a column with a width. These pin the rule that the
/// database is never the first validator: an over-long value must be a 400 (or 401) with a
/// stable errorCode, not a DbUpdateException surfacing as a 500. All four of these inputs
/// really did 500 before the length checks existed.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class InputValidationTests(PaymentsApiFixture fixture)
{
    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");

    [Fact]
    public async Task An_overlong_merchant_id_is_rejected_not_500()
    {
        // merchant_id columns are varchar(64); the actor string "merchant:{id}" has 128.
        var api = PaymentsApiClient.For(fixture.Api.CreateClient(), new string('m', 100));

        var response = await api.CreateRaw(PaymentsApiClient.NewPaymentBody(), Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("merchant_identity_required", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task An_overlong_idempotency_key_is_rejected_not_500()
    {
        var api = NewMerchant();

        // idempotency_keys.key is varchar(128).
        var response = await api.CreateRaw(PaymentsApiClient.NewPaymentBody(), new string('k', 200));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task An_overlong_description_is_a_domain_validation_error()
    {
        var api = NewMerchant();

        var response = await api.CreateRaw(
            PaymentsApiClient.NewPaymentBody(description: new string('d', 600)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_description", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task An_overlong_card_brand_is_a_domain_validation_error()
    {
        var api = NewMerchant();

        var response = await api.CreateRaw(
            new
            {
                amountMinor = 100L,
                currency = "USD",
                cardToken = "tok_visa",
                cardLast4 = "4242",
                cardBrand = new string('b', 40),
            },
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_card_brand", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task An_overlong_correlation_id_is_replaced_not_rejected()
    {
        var api = NewMerchant();

        // Tracing is best-effort: a bad tracing header shouldn't fail a payment. The request
        // succeeds, and the echoed header is the replacement id actually used — which also
        // means it's the id on the audit trail, so the caller can still trace the payment.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = JsonContent.Create(PaymentsApiClient.NewPaymentBody()),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-Id", new string('c', 100));

        var response = await api.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var echoed = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual(new string('c', 100), echoed);
        Assert.True(echoed.Length <= 64);

        var payment = await api.Read(response);
        var trail = await api.Transitions(payment.Id);
        Assert.All(trail, t => Assert.Equal(echoed, t.CorrelationId));
    }
}
