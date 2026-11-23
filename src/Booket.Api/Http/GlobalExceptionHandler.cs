using Booket.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Booket.Api.Http;

/// <summary>
/// Last line of defence: turns unhandled exceptions into ProblemDetails. Expected failures never
/// get here; they are returned as results by the handlers.
/// </summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, detail) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, bad.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource was modified concurrently. Reload and try again."),
            ExclusionConstraintViolationException => (StatusCodes.Status409Conflict, "The requested time slot is no longer available."),
            UniqueConstraintViolationException => (StatusCodes.Status409Conflict, "A resource with the same unique value already exists."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "The client closed the request."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("Request failed with {StatusCode}: {Message}", status, exception.Message);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail },
        });
    }
}
