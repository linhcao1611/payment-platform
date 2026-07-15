using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Infrastructure.Idempotency;

namespace Payments.Api.Middleware;

/// <summary>
/// Maps expected errors to RFC 7807 problem+json with a stable errorCode extension:
/// validation → 400; illegal state transition, idempotency conflict and lost concurrency
/// races → 409. Unexpected exceptions fall through to the default 500 handler.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        var mapped = exception switch
        {
            InvalidStateTransitionException e =>
                new Problem(StatusCodes.Status409Conflict, "Invalid payment state transition", e.Message, e.ErrorCode),
            IdempotencyConflictException e =>
                new Problem(StatusCodes.Status409Conflict, "Idempotency key conflict", e.Message, e.ErrorCode),
            DomainValidationException e =>
                new Problem(StatusCodes.Status400BadRequest, "Validation failed", e.Message, e.ErrorCode),
            // The payment's xmin token caught a concurrent write — e.g. a capture and a
            // refund racing. The loser is told to re-read and retry rather than clobbering.
            DbUpdateConcurrencyException =>
                new Problem(
                    StatusCodes.Status409Conflict,
                    "Concurrent modification",
                    "The payment was modified by another request. Re-read it and retry.",
                    "concurrent_modification"),
            _ => null,
        };

        if (mapped is null)
            return false;

        context.Response.StatusCode = mapped.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = mapped.Status,
                Title = mapped.Title,
                Detail = mapped.Detail,
                Extensions = { ["errorCode"] = mapped.ErrorCode },
            },
        });
    }

    private sealed record Problem(int Status, string Title, string Detail, string ErrorCode);
}
