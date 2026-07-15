using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Api.Tests;

/// <summary>Just enough of the wire contract to assert against, mirroring the API's camelCase.</summary>
public sealed record PaymentDto(
    Guid Id, string MerchantId, long AmountMinor, string Currency,
    string CardLast4, string CardBrand, string Status, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TransitionDto(
    string? FromStatus, string ToStatus, string Actor, string CorrelationId,
    string? Reason, DateTimeOffset OccurredAt);

public sealed record ProblemDto(string? Title, int? Status, string? Detail, string? ErrorCode);

/// <summary>
/// A thin typed client over the API. Tests drive real HTTP through the real pipeline —
/// middleware, model binding, the exception handler and problem+json shape all included,
/// because those are exactly the parts a controller-level unit test would skip.
/// </summary>
public sealed class PaymentsApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http => http;

    public static PaymentsApiClient For(HttpClient http, string merchantId)
    {
        http.DefaultRequestHeaders.Add("X-Merchant-Id", merchantId);
        return new PaymentsApiClient(http);
    }

    public Task<HttpResponseMessage> CreateRaw(object body, string? idempotencyKey) =>
        Send(HttpMethod.Post, "/api/payments", body, idempotencyKey);

    public Task<HttpResponseMessage> CaptureRaw(Guid id, string? idempotencyKey) =>
        Send(HttpMethod.Post, $"/api/payments/{id}/capture", null, idempotencyKey);

    public Task<HttpResponseMessage> RefundRaw(Guid id, string? reason, string? idempotencyKey) =>
        Send(HttpMethod.Post, $"/api/payments/{id}/refund", new { reason }, idempotencyKey);

    public Task<HttpResponseMessage> GetRaw(Guid id) =>
        http.GetAsync($"/api/payments/{id}");

    public async Task<PaymentDto> Get(Guid id)
    {
        var response = await GetRaw(id);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentDto>(Json))!;
    }

    public async Task<IReadOnlyList<TransitionDto>> Transitions(Guid id) =>
        (await http.GetFromJsonAsync<List<TransitionDto>>($"/api/payments/{id}/transitions", Json))!;

    /// <summary>Creates an Authorized payment. The fixture pins the gateway to always approve.</summary>
    public async Task<PaymentDto> CreateAuthorized(long amountMinor = 4200, string? description = null)
    {
        var response = await CreateRaw(NewPaymentBody(amountMinor, description), Guid.NewGuid().ToString());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentDto>(Json))!;
    }

    public static object NewPaymentBody(long amountMinor = 4200, string? description = null, string cardToken = "tok_visa") =>
        new
        {
            amountMinor,
            currency = "USD",
            cardToken,
            cardLast4 = "4242",
            cardBrand = "visa",
            description,
        };

    public static async Task<ProblemDto> Problem(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;

    /// <summary>Reads a payment out of a response the test already has in hand.</summary>
    public async Task<PaymentDto> Read(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PaymentDto>(Json))!;

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string url, object? body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, url);

        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        return await http.SendAsync(request);
    }
}
