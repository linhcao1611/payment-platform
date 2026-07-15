using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Payments.Api.Contracts;
using Payments.Api.Idempotency;
using Payments.Api.Middleware;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Gateway;
using Payments.Infrastructure.Idempotency;

namespace Payments.Api.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(
    IPaymentRepository payments,
    IIdempotencyStore idempotency,
    ISettlementQueue settlement,
    IPaymentGateway gateway,
    TimeProvider clock,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Stub for real authentication: merchant identity comes from a header.
    /// In production this would be an API key / OAuth2 client-credentials
    /// subject resolved by middleware.
    /// </summary>
    private const string MerchantHeader = "X-Merchant-Id";

    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Signals to the caller that a response came from the store, not from new work.</summary>
    private const string ReplayHeader = "Idempotency-Replayed";

    /// <summary>
    /// MVC's own serializer options, so a replayed body is byte-identical to the one the
    /// original request returned rather than merely equivalent.
    /// </summary>
    private readonly JsonSerializerOptions _json = jsonOptions.Value.JsonSerializerOptions;

    private string? MerchantId =>
        Request.Headers.TryGetValue(MerchantHeader, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;

    private string? RequestIdempotencyKey =>
        Request.Headers.TryGetValue(IdempotencyHeader, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;

    [HttpPost]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreatePaymentRequest request, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();
        if (RequestIdempotencyKey is not { } key)
            return MissingIdempotencyKey();

        var correlationId = CorrelationIdMiddleware.Get(HttpContext);
        var actor = $"merchant:{merchantId}";

        Payment? created = null;

        var result = await idempotency.ExecuteAsync(
            merchantId, "create", key,
            RequestHash.Compute("create", merchantId, request),
            async token =>
            {
                var payment = Payment.Create(
                    merchantId, request.AmountMinor, request.Currency,
                    request.CardLast4, request.CardBrand, request.Description,
                    actor, correlationId, clock.GetUtcNow());

                // Synchronous authorization at create time (auth-then-capture model).
                var auth = await gateway.AuthorizeAsync(
                    request.CardToken, request.AmountMinor, request.Currency, token);

                if (auth.Approved)
                    payment.Authorize(actor, correlationId, clock.GetUtcNow());
                else
                    payment.Fail(actor, correlationId, auth.DeclineReason ?? "declined", clock.GetUtcNow());

                payments.Add(payment);
                await payments.SaveAsync(payment, token);

                logger.LogInformation(
                    "Payment {PaymentId} created with status {PaymentStatus} for merchant {MerchantId}",
                    payment.Id, payment.Status, merchantId);

                created = payment;
                return new IdempotentOutcome(
                    StatusCodes.Status201Created, JsonSerializer.Serialize(PaymentResponse.From(payment), _json));
            },
            ct);

        if (created is not null)
            Response.Headers.Location = Url.Action(nameof(Get), new { id = created.Id });

        return Replayable(result);
    }

    [HttpPost("{id:guid}/capture")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Capture(Guid id, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();
        if (RequestIdempotencyKey is not { } key)
            return MissingIdempotencyKey();

        // Resolve the payment before reserving the key: a 404 is a fact about the URL, not
        // an outcome worth replaying for the lifetime of the key. The read is outside the
        // transaction, but the payment's xmin token turns a stale read into a 409 at save.
        var payment = await payments.GetAsync(id, merchantId, ct);
        if (payment is null)
            return PaymentNotFound(id);

        var correlationId = CorrelationIdMiddleware.Get(HttpContext);
        var actor = $"merchant:{merchantId}";

        var result = await idempotency.ExecuteAsync(
            merchantId, "capture", key,
            RequestHash.Compute("capture", merchantId, new CaptureCommand(id)),
            async token =>
            {
                payment.Capture(actor, correlationId, clock.GetUtcNow());
                settlement.Enqueue(payment.Id, correlationId, clock.GetUtcNow());
                await payments.SaveAsync(payment, token);

                logger.LogInformation(
                    "Payment {PaymentId} captured for merchant {MerchantId}; settlement job enqueued",
                    payment.Id, merchantId);

                return new IdempotentOutcome(
                    StatusCodes.Status200OK, JsonSerializer.Serialize(PaymentResponse.From(payment), _json));
            },
            ct);

        return Replayable(result);
    }

    [HttpPost("{id:guid}/refund")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refund(Guid id, RefundRequest? request, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();
        if (RequestIdempotencyKey is not { } key)
            return MissingIdempotencyKey();

        var payment = await payments.GetAsync(id, merchantId, ct);
        if (payment is null)
            return PaymentNotFound(id);

        var correlationId = CorrelationIdMiddleware.Get(HttpContext);
        var actor = $"merchant:{merchantId}";
        var reason = request?.Reason;

        var result = await idempotency.ExecuteAsync(
            merchantId, "refund", key,
            RequestHash.Compute("refund", merchantId, new RefundCommand(id, reason)),
            async token =>
            {
                // Refunding a Settled payment would call the gateway in a real integration;
                // the state change and audit trail are what this exercise is demonstrating.
                payment.Refund(actor, correlationId, reason, clock.GetUtcNow());
                await payments.SaveAsync(payment, token);

                logger.LogInformation(
                    "Payment {PaymentId} refunded for merchant {MerchantId}", payment.Id, merchantId);

                return new IdempotentOutcome(
                    StatusCodes.Status200OK, JsonSerializer.Serialize(PaymentResponse.From(payment), _json));
            },
            ct);

        return Replayable(result);
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

    /// <summary>
    /// Returns the stored status and body verbatim. The store keeps status + body, not the
    /// full header set, so a replayed create omits Location — a deliberate simplification,
    /// matching how PSPs like Stripe replay (status, body, and a "this was a replay" flag).
    /// </summary>
    private ContentResult Replayable(IdempotentResult result)
    {
        if (result.Replayed)
            Response.Headers[ReplayHeader] = "true";

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            Content = result.ResponseBody,
            ContentType = "application/json",
        };
    }

    private ObjectResult MissingMerchant() =>
        ProblemWithCode(
            StatusCodes.Status401Unauthorized,
            "Missing merchant identity",
            $"The {MerchantHeader} header is required.",
            "merchant_identity_required");

    private ObjectResult MissingIdempotencyKey() =>
        ProblemWithCode(
            StatusCodes.Status400BadRequest,
            "Missing idempotency key",
            $"The {IdempotencyHeader} header is required on this operation.",
            "idempotency_key_required");

    private ObjectResult PaymentNotFound(Guid id) =>
        ProblemWithCode(
            StatusCodes.Status404NotFound,
            "Payment not found",
            $"No payment {id} exists for this merchant.",
            "payment_not_found");

    /// <summary>
    /// problem+json carrying the same stable <c>errorCode</c> extension that
    /// <see cref="DomainExceptionHandler"/> attaches, so clients can branch on one field
    /// regardless of whether the rejection came from the domain or from the edge.
    /// </summary>
    private ObjectResult ProblemWithCode(int status, string title, string detail, string errorCode)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
    }
}
