namespace Payments.Api.Middleware;

/// <summary>
/// Reads X-Correlation-Id (or generates one), exposes it via HttpContext,
/// pushes it into the logging scope, and echoes it on the response. The same
/// id is persisted onto audit transitions and settlement jobs, which is what
/// lets a single payment's journey be traced across the async boundary.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    /// <summary>
    /// The id is persisted onto audit rows and settlement jobs, whose columns are varchar(64).
    /// A longer client value would fail that insert deep inside the request — a 500 from a
    /// tracing header. Tracing is best-effort, so an over-long id is replaced with a fresh one
    /// rather than rejected; the caller still gets the id actually used echoed back.
    /// </summary>
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header)
            && header.ToString() is { Length: <= MaxLength } supplied
                ? supplied
                : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items[ItemKey] as string ?? "unknown";
}
