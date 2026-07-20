using System.Diagnostics;
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
using Payments.Infrastructure.Observability;

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

    // Header lengths are capped to the column widths they end up in (merchant_id/actor and
    // idempotency_keys.key). Over-long values are treated the same as missing ones — the
    // rejection message names the limit — because the alternative is the insert failing deep
    // in the transaction and surfacing as a 500 with no errorCode.
    private const int MaxMerchantIdLength = 64;
    private const int MaxIdempotencyKeyLength = 128;

    private string? MerchantId =>
        Request.Headers.TryGetValue(MerchantHeader, out var v)
            && !string.IsNullOrWhiteSpace(v)
            && v.ToString() is { Length: <= MaxMerchantIdLength } merchant
                ? merchant
                : null;

    private string? RequestIdempotencyKey =>
        Request.Headers.TryGetValue(IdempotencyHeader, out var v)
            && !string.IsNullOrWhiteSpace(v)
            && v.ToString() is { Length: <= MaxIdempotencyKeyLength } key
                ? key
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

        // Counted here rather than inside the work: this runs only once the transaction has
        // committed, and `created` is null on a replay — so a client hammering retries moves
        // the counter exactly once, the same as it moves the payment exactly once.
        if (created is not null)
        {
            PaymentsMetrics.Created.Inc();
            (created.Status is PaymentStatus.Authorized
                ? PaymentsMetrics.Authorized
                : PaymentsMetrics.Failed).Inc();

            // Same tag keys SettlementWorker already puts on its settle-payment span, so one
            // TraceQL query finds every trace touching this payment, not just settlement's.
            Activity.Current?.SetTag("payment.id", created.Id);
            Activity.Current?.SetTag("correlation.id", correlationId);

            Response.Headers.Location = Url.Action(nameof(Get), new { id = created.Id });
        }

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

        // Unlike Create, id is a route parameter here - known up front regardless of whether
        // this turns out to be a replay, so there's no reason to skip tagging on one.
        Activity.Current?.SetTag("payment.id", id);
        Activity.Current?.SetTag("correlation.id", correlationId);

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

        if (!result.Replayed)
            PaymentsMetrics.Captured.Inc();

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

        Activity.Current?.SetTag("payment.id", id);
        Activity.Current?.SetTag("correlation.id", correlationId);

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

        if (!result.Replayed)
            PaymentsMetrics.Refunded.Inc();

        return Replayable(result);
    }

    /// <summary>Maximum page size. A caller asking for more gets this, not an error.</summary>
    private const int MaxPageSize = 100;

    [HttpGet]
    [ProducesResponseType<PagedResponse<PaymentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListPaymentsRequest request, CancellationToken ct)
    {
        if (MerchantId is not { } merchantId)
            return MissingMerchant();

        PaymentStatus? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<PaymentStatus>(request.Status, ignoreCase: true, out var parsed))
                return ProblemWithCode(
                    StatusCodes.Status400BadRequest,
                    "Invalid status filter",
                    $"'{request.Status}' is not a payment status. Valid values: {string.Join(", ", Enum.GetNames<PaymentStatus>())}.",
                    "invalid_status");

            status = parsed;
        }

        if (request.From is { } from && request.To is { } to && from > to)
            return ProblemWithCode(
                StatusCodes.Status400BadRequest,
                "Invalid date range",
                "'from' must not be later than 'to'.",
                "invalid_date_range");

        // Clamped rather than rejected: a client asking for 10,000 rows gets 100, not a 400.
        // The cap exists so one caller can't ask the database for the whole table.
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        var page = Math.Max(request.Page, 1);

        // Normalized to UTC at the boundary. Npgsql refuses to bind a DateTimeOffset with a
        // non-zero offset to `timestamp with time zone`, so a caller sending its own local
        // offset — a browser in Berlin sending +02:00, or a bare yyyy-MM-dd that model binding
        // stamps with the *server's* offset — would otherwise 500 deep in the query. The
        // instant is preserved; only the offset changes, which is all the column stores anyway.
        var result = await payments.ListAsync(
            new PaymentQuery(
                merchantId,
                status,
                request.From?.ToUniversalTime(),
                request.To?.ToUniversalTime(),
                request.Search,
                page,
                pageSize),
            ct);

        return Ok(new PagedResponse<PaymentResponse>(
            result.Items.Select(PaymentResponse.From).ToList(),
            result.Page,
            result.PageSize,
            result.TotalCount,
            TotalPages: (int)Math.Ceiling(result.TotalCount / (double)result.PageSize)));
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
            "Missing or invalid merchant identity",
            $"The {MerchantHeader} header is required and must be at most {MaxMerchantIdLength} characters.",
            "merchant_identity_required");

    private ObjectResult MissingIdempotencyKey() =>
        ProblemWithCode(
            StatusCodes.Status400BadRequest,
            "Missing or invalid idempotency key",
            $"The {IdempotencyHeader} header is required on this operation and must be at most {MaxIdempotencyKeyLength} characters.",
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
