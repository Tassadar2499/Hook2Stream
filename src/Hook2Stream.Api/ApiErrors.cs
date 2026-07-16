using System.Diagnostics;
using Hook2Stream.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Api;

public sealed class ApiProblemException(
    int statusCode,
    string code,
    string safeMessage,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(safeMessage)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ApiProblemException apiProblem => (
                apiProblem.StatusCode,
                apiProblem.Code,
                apiProblem.SafeMessage,
                apiProblem.Errors),
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "concurrency.conflict",
                "The resource changed. Reload it and retry with the latest ETag.",
                null),
            _ => (
                StatusCodes.Status500InternalServerError,
                "server.unexpected",
                "An unexpected error occurred.",
                null)
        };

        if (problem.Item1 >= 500)
        {
            logger.LogError(exception, "Unhandled API error. TraceId: {TraceId}", Activity.Current?.TraceId);
        }
        else
        {
            logger.LogInformation(
                exception,
                "API request rejected with {Code}. TraceId: {TraceId}",
                problem.Item2,
                Activity.Current?.TraceId);
        }

        httpContext.Response.StatusCode = problem.Item1;
        var details = new ProblemDetails
        {
            Status = problem.Item1,
            Title = problem.Item2,
            Detail = problem.Item3,
            Instance = httpContext.Request.Path
        };
        details.Extensions["code"] = problem.Item2;
        details.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        if (problem.Item4 is not null)
        {
            details.Extensions["errors"] = problem.Item4;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details
        });
    }
}

public static class ApiEndpointHelpers
{
    public static void RequireValid(ValidationErrors errors)
    {
        if (!errors.IsValid)
        {
            throw new ApiProblemException(
                StatusCodes.Status422UnprocessableEntity,
                "validation.failed",
                "One or more fields are invalid.",
                errors.ToDictionary());
        }
    }

    public static long RequireIfMatch(HttpRequest request)
    {
        var value = request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ApiProblemException(
                StatusCodes.Status428PreconditionRequired,
                "concurrency.if_match_required",
                "Send the current ETag in the If-Match header.");
        }

        value = value.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');
        if (!long.TryParse(value, out var version) || version < 1)
        {
            throw new ApiProblemException(
                StatusCodes.Status400BadRequest,
                "concurrency.if_match_invalid",
                "If-Match must contain a valid resource version.");
        }

        return version;
    }

    public static void EnsureVersion(long expected, long actual)
    {
        if (expected != actual)
        {
            throw new ApiProblemException(
                StatusCodes.Status412PreconditionFailed,
                "concurrency.etag_mismatch",
                "The resource changed. Reload it and retry.");
        }
    }

    public static void SetEtag(HttpResponse response, long version) =>
        response.Headers.ETag = $"\"{version}\"";
}
