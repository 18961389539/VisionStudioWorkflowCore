using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;

namespace VisionStudio.Api.Tests;

public sealed class SecurityTests
{

    [Fact]
    public async Task ProtectedApi_RequiresAuthentication_ButStatusAndHealthAreAnonymous()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var client = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("bootstrapRequired").GetBoolean());

        var protectedResponse = await client.GetAsync("/api/catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        Assert.Equal("application/problem+json", protectedResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Bootstrap_IsOneShot_AndLoginCreatesAuthenticatedSession()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var client = factory.CreateFreshClient();
        var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username = "admin", displayName = "Plant Administrator", password = "AdminPass!234" });
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);

        var secondBootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username = "other", displayName = "Other", password = "OtherPass!234" });
        Assert.Equal(HttpStatusCode.Conflict, secondBootstrap.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "AdminPass!234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("vs_session=", string.Join(";", login.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Equal("Administrator", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Operator_CannotUseEngineerDebugEndpoint()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var create = await admin.PostAsJsonAsync("/api/security/users", new { username = "operator1", displayName = "Operator One", role = "Operator", password = "Operator!234" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var op = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/auth/login", new { username = "operator1", password = "Operator!234" })).StatusCode);
        var compile = await op.PostAsJsonAsync("/api/compile", new { id = "x", name = "x", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, compile.StatusCode);
        Assert.Equal("application/problem+json", compile.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/production/status")).StatusCode);
    }

    [Fact]
    public async Task LastEnabledAdministrator_CannotBeDemoted()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        var user = await EnsureAdminAsync(admin);
        var response = await admin.PutAsJsonAsync($"/api/security/users/{user.GetProperty("id").GetString()}", new { displayName = "Plant Administrator", role = "Engineer", enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentAdministratorDemotions_CannotRemoveLastAdministrators()
    {
        // F09 回归：两名并发降级必须至少保留一名启用管理员。若检查与 UPDATE 不在同一写事务中，
        // 两路请求都能读到"还有另一名管理员"并依次降级。事务化（BEGIN IMMEDIATE）后其中一路必然冲突。
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        var first = await EnsureAdminAsync(admin);
        var secondResponse = await admin.PostAsJsonAsync("/api/security/users", new { username = "admin2", displayName = "Second Admin", role = "Administrator", password = "Admin2!234567" });
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();

        var demoteFirst = admin.PutAsJsonAsync($"/api/security/users/{first.GetProperty("id").GetString()}", new { displayName = "Plant Administrator", role = "Engineer", enabled = true });
        var demoteSecond = admin.PutAsJsonAsync($"/api/security/users/{second.GetProperty("id").GetString()}", new { displayName = "Second Admin", role = "Engineer", enabled = true });
        var responses = await Task.WhenAll(demoteFirst, demoteSecond);
        // 两路并发降级必有一路失败：事务串行化会拒绝其一（409）；若被降级的管理员恰是请求发起者，
        // 其后续请求还可能在授权层被拒（403）——两者都表示"最后的管理员未被移除"。
        Assert.Contains(responses, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);

        // 直接查库断言（降级后的会话可能不再是管理员，不能再走列表 API）。
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM security_users WHERE enabled=1 AND role='Administrator';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AuditedMutation_IsQueryableByAdministrator()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var create = await admin.PostAsJsonAsync("/api/security/users", new { username = "engineer1", displayName = "Engineer One", role = "Engineer", password = "Engineer!234" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/audit?limit=100&action=security.user.create");
        Assert.Equal(JsonValueKind.Array, audit.ValueKind);
        Assert.Contains(audit.EnumerateArray(), item => item.GetProperty("action").GetString() == "security.user.create" && item.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task AutoLogin_IsUnavailable_WhenTheOptionIsOff()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var client = factory.CreateFreshClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(status.GetProperty("autoLogin").GetBoolean());

        var response = await client.PostAsync("/api/auth/auto-login", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/catalog")).StatusCode);
    }

    [Fact]
    public async Task AutoLogin_CreatesBuiltInAdministrator_AndIssuesAdministratorSession()
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true);
        using var client = factory.CreateFreshClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("autoLogin").GetBoolean());
        Assert.True(status.GetProperty("bootstrapRequired").GetBoolean());

        var response = await client.PostAsync("/api/auth/auto-login", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("vs_session=", string.Join(";", response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Equal("Administrator", me.GetProperty("role").GetString());

        // 自动登录直接拿到管理员权限，且首初始化入口随之关闭
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/security/users")).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.False(after.GetProperty("bootstrapRequired").GetBoolean());
    }

    [Fact]
    public async Task AutoLogin_RefusesToEscalate_ExistingNonAdministratorAccount()
    {
        using var factory = new SecureVisionStudioApiFactory(autoLoginAdmin: true, autoLoginUsername: "lineoperator");
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var create = await admin.PostAsJsonAsync("/api/security/users", new { username = "lineoperator", displayName = "Line Operator", role = "Operator", password = "Operator!234" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var client = factory.CreateFreshClient();
        var response = await client.PostAsync("/api/auth/auto-login", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/catalog")).StatusCode);
    }

    private static async Task<JsonElement> EnsureAdminAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        if (status.GetProperty("bootstrapRequired").GetBoolean())
        {
            var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username = "admin", displayName = "Plant Administrator", password = "AdminPass!234" });
            Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        }
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "AdminPass!234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
    }
}

public sealed class SecureVisionStudioApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "visionstudio-security-tests", Guid.NewGuid().ToString("N"));
    private readonly bool _autoLoginAdmin;
    private readonly string? _autoLoginUsername;

    public SecureVisionStudioApiFactory(bool autoLoginAdmin = false, string? autoLoginUsername = null)
    {
        _autoLoginAdmin = autoLoginAdmin;
        _autoLoginUsername = autoLoginUsername;
        Directory.CreateDirectory(_root);
    }

    public HttpClient CreateFreshClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_root);
        builder.UseSetting("Security:Enabled", "true");
        builder.UseSetting("Security:AutoLoginAdmin", _autoLoginAdmin ? "true" : "false");
        if (_autoLoginUsername is not null) builder.UseSetting("Security:AutoLoginUsername", _autoLoginUsername);
        builder.UseSetting("Security:PasswordPbkdf2Iterations", "100000");
        builder.UseSetting("Security:SessionHours", "1");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
