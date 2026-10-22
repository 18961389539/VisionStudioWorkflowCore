using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Security;

public sealed class VisionStudioAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SecurityStore store,
    IOptions<SecurityOptions> securityOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "VisionStudioBearer";
    private readonly SecurityOptions _security = securityOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_security.Enabled)
            return AuthenticateResult.Success(CreateTicket("system", "Security Disabled", SecurityRoles.Administrator));

        var token = Request.Cookies[_security.CookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();
        var user = await store.ResolveSessionAsync(token, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Invalid or expired session.");
        return AuthenticateResult.Success(CreateTicket(user.Id, user.Username, user.Role, user.DisplayName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => WriteProblemAsync(StatusCodes.Status401Unauthorized, "Unauthorized", "Authentication is required.", "unauthorized");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => WriteProblemAsync(StatusCodes.Status403Forbidden, "Forbidden", "Your role does not permit this operation.", "forbidden");

    private AuthenticationTicket CreateTicket(string id, string username, string role, string? displayName = null)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role), new Claim("display_name", displayName ?? username) };
        return new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme);
    }

    private async Task WriteProblemAsync(int status, string title, string detail, string code)
    {
        Response.StatusCode = status; Response.ContentType = "application/problem+json";
        var correlationId = RequestCorrelation.Get(Context);
        Response.Headers["X-Correlation-ID"] = correlationId;
        await Response.WriteAsync(JsonSerializer.Serialize(new { type = $"https://httpstatuses.com/{status}", title, status, detail, code, correlationId }));
    }
}
