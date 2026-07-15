using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Payments.Domain;

namespace Payments.Api.Middleware;

/// <summary>
/// Maps domain errors to RFC 7807 problem+json with a stable errorCode
/// extension: validation → 400, illegal state transition → 409.
/// Unexpected exceptions fall through to the default 500 handler.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        (int status, string title) = exception switch
        {
            InvalidStateTransitionException => (StatusCodes.Status409Conflict, "Invalid payment state transition"),
            DomainValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            _ => (0, string.Empty),
        };

        if (status == 0 || exception is not DomainException domainException)
            return false;

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = domainException.Message,
                Extensions = { ["errorCode"] = domainException.ErrorCode },
            },
        });
    }
}
