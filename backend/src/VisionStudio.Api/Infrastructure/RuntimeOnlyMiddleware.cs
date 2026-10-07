using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VisionStudio.Api.Infrastructure;

public sealed class RuntimeOnlyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly bool _runtimeOnly = configuration.GetValue("VisionStudio:RuntimeOnly", false);

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        var path = context.Request.Path;
        var safeRead = HttpMethods.IsGet(context.Request.Method) ||
                       HttpMethods.IsHead(context.Request.Method) ||
                       HttpMethods.IsOptions(context.Request.Method);

        if (_runtimeOnly && path.StartsWithSegments("/api") && !safeRead && !RuntimeOnlyPolicy.IsMutationAllowed(path, context.Request.Method))
        {
            var correlationId = Activity.Current?.Id ?? context.TraceIdentifier;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers["X-Correlation-ID"] = correlationId;

            var details = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Runtime-only host",
                Detail = "This mutation is disabled. Runtime-only hosts allow production operations, alarm acknowledgement and security/session administration only.",
                Type = "https://httpstatuses.com/403",
                Instance = path
            };
            details.Extensions["code"] = "runtime_only";
            details.Extensions["correlationId"] = correlationId;

            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = details
            });
            return;
        }

        await next(context);
    }
}

public static class RuntimeOnlyPolicy
{
    public static bool IsMutationAllowed(PathString path, string method)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsPost(method);
        if (value.StartsWith("/api/security/", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method);
        if (value.StartsWith("/api/storage/", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("/api/storage/cleanup", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
            if (value.Equals("/api/storage/backups", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
            if (value.StartsWith("/api/storage/backups/", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsDelete(method)) return true;
            if (value.Equals("/api/storage/restore", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        }
        if (!HttpMethods.IsPost(method)) return false;
        if (value.Equals("/api/production/start", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("/api/production/stop", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("/api/production/recover", StringComparison.OrdinalIgnoreCase)) return true;
        return value.StartsWith("/api/alarms/", StringComparison.OrdinalIgnoreCase) &&
               value.EndsWith("/ack", StringComparison.OrdinalIgnoreCase);
    }
}
