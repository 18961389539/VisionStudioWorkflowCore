using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Tests;

public sealed class CookieCsrfTests
{
    [Theory]
    [InlineData("http://localhost:5173", null, "same-site")]
    [InlineData("http://localhost:5173", null, null)]
    [InlineData("https://localhost", null, null)]
    [InlineData("http://other.example", null, null)]
    [InlineData("null", null, "same-origin")]
    [InlineData("not-an-origin", "http://localhost/", null)]
    [InlineData("http://localhost/unexpected-path", null, null)]
    [InlineData("http://localhost http://other.example", null, null)]
    [InlineData(null, "http://localhost:5173/page", null)]
    [InlineData(null, null, "same-site")]
    [InlineData(null, null, "cross-site")]
    public async Task BodylessCookieLogout_RejectsForeignOrInvalidProvenance_WithoutRevokingSession(
        string? origin, string? referer, string? fetchSite)
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var client = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/auto-login", null)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("csrf_origin_rejected", problem.GetProperty("code").GetString());
        Assert.Equal(problem.GetProperty("correlationId").GetString(), Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost", null, "same-origin")]
    [InlineData("http://LOCALHOST:80", null, null)]
    [InlineData(null, "http://localhost/settings?tab=security", null)]
    [InlineData(null, null, "same-origin")]
    [InlineData(null, null, null)]
    public async Task BodylessCookieLogout_AllowsSameOriginAndNonBrowserClients(string? origin, string? referer, string? fetchSite)
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var client = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/auto-login", null)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task CookieReadsRemainAvailable_ButAnUnrelatedAuthorizationHeaderDoesNotBypassWriteProtection()
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var client = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/auto-login", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost:5173");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-session");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        var blocked = await client.PostAsync("/api/production/stop", null);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("csrf_origin_rejected", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExplicitHumanBearer_StillWorksWithoutCookieAuthentication()
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsync("/api/auth/auto-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(login.Headers.GetValues("Set-Cookie")));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cookie.Value.ToString());
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ExplicitServiceBearer_TakesPrecedenceOverAmbientCookie()
    {
        using var factory = new SecureVisionStudioApiFactory();
        var tokens = factory.Services.GetRequiredService<ServiceTokenStore>();
        var token = await tokens.CreateAsync(new CreateServiceTokenRequest("controller", [ServiceScopes.StationControl], null), "test");
        using var client = factory.CreateFreshClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        client.DefaultRequestHeaders.Add("Cookie", "vs_session=unrelated-cookie");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/production/stop", null)).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("https://localhost")]
    public async Task LogoutCookie_MatchesLoginScopeAndSecurityAttributes(string baseAddress)
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress) });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/bootstrap",
            new { username = "admin", displayName = "Admin", password = "AdminPass!234" })).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "AdminPass!234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(login.Headers.GetValues("Set-Cookie")));
        client.DefaultRequestHeaders.Add("Origin", baseAddress);
        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var deleted = SetCookieHeaderValue.Parse(Assert.Single(logout.Headers.GetValues("Set-Cookie")));

        Assert.Equal(issued.Name, deleted.Name);
        Assert.Equal(issued.Path, deleted.Path);
        Assert.Equal("/", issued.Path.ToString());
        Assert.Equal(issued.Domain, deleted.Domain);
        Assert.Equal(issued.SameSite, deleted.SameSite);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, issued.SameSite);
        Assert.True(issued.HttpOnly && deleted.HttpOnly);
        Assert.Equal(baseAddress.StartsWith("https:", StringComparison.Ordinal), issued.Secure);
        Assert.Equal(issued.Secure, deleted.Secure);
        Assert.True(issued.Expires > DateTimeOffset.UtcNow);
        Assert.True(deleted.Expires < DateTimeOffset.UtcNow);
        Assert.Empty(deleted.Value.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task TrustedProxy_UsesForwardedSchemeHostAndPort_ForOriginAndCookieSecurity()
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var proxyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ReverseProxy:Enabled", "true");
            builder.UseSetting("ReverseProxy:ForwardHost", "true");
        });
        using var client = proxyFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "plant.example:8443");
        var login = await client.PostAsync("/api/auth/auto-login", null);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(login.Headers.GetValues("Set-Cookie")));
        Assert.True(issued.Secure);
        client.DefaultRequestHeaders.Add("Cookie", $"{issued.Name}={issued.Value}");
        using var attack = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        attack.Headers.Add("Origin", "https://plant.example:8444");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(attack)).StatusCode);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Origin", "https://plant.example:8443");
        var response = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues("Set-Cookie"))).Secure);
    }
}
