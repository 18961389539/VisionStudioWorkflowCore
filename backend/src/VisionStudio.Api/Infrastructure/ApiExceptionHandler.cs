using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Device;

namespace VisionStudio.Api.Infrastructure;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return false;

        var classification = Classify(exception);
        // 与审计记录/其它中间件共用同一请求级关联 ID（首次计算后缓存），响应与审计永远可互相匹配
        var correlationId = RequestCorrelation.Get(httpContext);

        if (classification.Status >= 500)
            logger.LogError(exception, "Unhandled API exception. CorrelationId={CorrelationId}", correlationId);
        else
            logger.LogWarning(exception, "API request failed with {Status}. CorrelationId={CorrelationId}", classification.Status, correlationId);

        httpContext.Response.StatusCode = classification.Status;
        httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

        var details = new ProblemDetails
        {
            Status = classification.Status,
            Title = classification.Title,
            Type = $"https://httpstatuses.com/{classification.Status}",
            Detail = classification.ExposeDetail ? exception.Message : "An unexpected server error occurred.",
            Instance = httpContext.Request.Path
        };
        details.Extensions["code"] = classification.Code;
        details.Extensions["correlationId"] = correlationId;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details
        });
    }

    private static ApiProblemClassification Classify(Exception exception)
    {
        if (exception is ApiProblemException api)
            return new(api.StatusCode, api.Title, api.Code, true);

        return exception switch
        {
            KeyNotFoundException => new(StatusCodes.Status404NotFound, "Resource not found", "not_found", true),
            DeviceDisconnectedException => new(StatusCodes.Status503ServiceUnavailable, "Device unavailable", "device_unavailable", true),
            TimeoutException => new(StatusCodes.Status503ServiceUnavailable, "Operation timed out", "timeout", true),
            SocketException => new(StatusCodes.Status503ServiceUnavailable, "Network dependency unavailable", "network_unavailable", true),
            HttpRequestException => new(StatusCodes.Status503ServiceUnavailable, "External dependency unavailable", "dependency_unavailable", true),
            SqliteException { SqliteErrorCode: 5 or 6 } => new(StatusCodes.Status503ServiceUnavailable, "Metadata store busy", "storage_busy", true),
            SqliteException { SqliteErrorCode: 19 } => new(StatusCodes.Status409Conflict, "Metadata conflict", "storage_conflict", true),
            ArgumentException => new(StatusCodes.Status400BadRequest, "Invalid request", "invalid_argument", true),
            FormatException => new(StatusCodes.Status400BadRequest, "Invalid request format", "invalid_format", true),
            JsonException => new(StatusCodes.Status400BadRequest, "Invalid JSON payload", "invalid_json", true),
            InvalidOperationException => new(StatusCodes.Status409Conflict, "Operation conflicts with current state", "invalid_state", true),
            _ => new(StatusCodes.Status500InternalServerError, "Internal server error", "internal_error", false)
        };
    }

    private sealed record ApiProblemClassification(int Status, string Title, string Code, bool ExposeDetail);
}
