using Microsoft.AspNetCore.Mvc;
using Payments.Api.Contracts;
using Payments.Api.Middleware;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Gateway;

namespace Payments.Api.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    TimeProvider clock,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Stub for real authentication: merchant identity comes from a header.
    /// In production this would be an API key / OAuth2 client-credentials
    /// subject resolved by middleware.
    /// </summary>
    private const string MerchantHeader = "X-Merchant-Id";

    private string? MerchantId =>
        Request.Headers.TryGetValue(MerchantHeader, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;

    [HttpPost]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create(CreatePaymentRequest request, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();

        var correlationId = CorrelationIdMiddleware.Get(HttpContext);
        var actor = $"merchant:{merchantId}";
        var now = clock.GetUtcNow();

        var payment = Payment.Create(
            merchantId, request.AmountMinor, request.Currency,
            request.CardLast4, request.CardBrand, request.Description,
            actor, correlationId, now);

        // Synchronous authorization at create time (auth-then-capture model).
        var result = await gateway.AuthorizeAsync(request.CardToken, request.AmountMinor, request.Currency, ct);
        if (result.Approved)
            payment.Authorize(actor, correlationId, clock.GetUtcNow());
        else
            payment.Fail(actor, correlationId, result.DeclineReason ?? "declined", clock.GetUtcNow());

        payments.Add(payment);
        await payments.SaveAsync(payment, ct);

        logger.LogInformation(
            "Payment {PaymentId} created with status {PaymentStatus} for merchant {MerchantId}",
            payment.Id, payment.Status, merchantId);

        return CreatedAtAction(nameof(Get), new { id = payment.Id }, PaymentResponse.From(payment));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();

        var payment = await payments.GetAsync(id, merchantId, ct);
        return payment is null ? PaymentNotFound(id) : Ok(PaymentResponse.From(payment));
    }

    [HttpGet("{id:guid}/transitions")]
    [ProducesResponseType<IReadOnlyList<TransitionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransitions(Guid id, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();

        var payment = await payments.GetAsync(id, merchantId, ct);
        if (payment is null)
            return PaymentNotFound(id);

        var transitions = await payments.GetTransitionsAsync(id, ct);
        return Ok(transitions.Select(TransitionResponse.From).ToList());
    }

    private ObjectResult MissingMerchant() =>
        Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Missing merchant identity",
            detail: $"The {MerchantHeader} header is required.");

    private ObjectResult PaymentNotFound(Guid id) =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Payment not found",
            detail: $"No payment {id} exists for this merchant.");
}
