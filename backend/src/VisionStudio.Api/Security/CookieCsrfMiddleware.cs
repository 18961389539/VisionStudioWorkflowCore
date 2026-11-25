using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Security;

public sealed class CookieCsrfMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService, ILogger<CookieCsrfMiddleware> logger)
    {
        var request = context.Request;
        var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
                         HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method);
        // Service Bearer tokens take precedence over cookies in the authentication handler.
        var cookieAuthenticated = options.Value.Enabled && context.User.Identity?.IsAuthenticated == true &&
            !context.User.HasClaim(claim => claim.Type == SecurityClaims.ServiceTokenId) &&
            !string.IsNullOrWhiteSpace(request.Cookies[options.Value.CookieName]);
        if (safeMethod || !cookieAuthenticated || HasSameOrigin(request))
        {
            await next(context);
            return;
        }

        var correlationId = RequestCorrelation.Get(context);
        logger.LogWarning("Blocked cross-origin cookie mutation {Method} {Path}. CorrelationId={CorrelationId}",
            request.Method, request.Path, correlationId);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Cross-origin cookie request blocked",
            Detail = "Cookie-authenticated mutations must originate from the same scheme, host and port.",
            Type = "https://httpstatuses.com/403",
            Instance = request.Path
        };
        details.Extensions["code"] = "csrf_origin_rejected";
        details.Extensions["correlationId"] = correlationId;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details });
    }

    private static bool HasSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 0 && (fetchSite.Count != 1 || fetchSite[0] != "same-origin")) return false;
        if (request.Headers.TryGetValue("Origin", out var origin)) return MatchesOrigin(request, origin, allowPath: false);
        if (request.Headers.TryGetValue("Referer", out var referer)) return MatchesOrigin(request, referer, allowPath: true);
        // Non-browser clients may send cookies without browser provenance headers.
        return true;
    }

    private static bool MatchesOrigin(HttpRequest request, StringValues header, bool allowPath)
    {
        if (header.Count != 1 || !Uri.TryCreate(header[0], UriKind.Absolute, out var source) ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps) ||
            source.UserInfo.Length != 0 ||
            (!allowPath && (source.AbsolutePath != "/" || source.Query.Length != 0 || source.Fragment.Length != 0)) ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host.ToUriComponent()}", UriKind.Absolute, out var target))
            return false;
        return source.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) &&
               source.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase) && source.Port == target.Port;
    }
}
