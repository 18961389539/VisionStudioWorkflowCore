using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 审计防篡改（P3-7）：每行挂在上一行哈希上；直接改库/删行必须被 /api/security/audit/verify 指出。
/// 注意：能整体重算链的攻击者仍可伪造，见 docs/audit-integrity.md 的边界说明。
/// </summary>
public sealed class AuditChainTests
{
    [Fact]
    public async Task ConsecutiveEvents_FormAValidChain()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AuditEventStore(new SqliteMetadataDatabase(env));
        await RecordAsync(store, "first");
        await RecordAsync(store, "second");
        await RecordAsync(store, "third");

        var result = await store.VerifyChainAsync();

        Assert.True(result.Valid);
        Assert.Equal(3, result.Checked);
        Assert.Null(result.FirstInvalidSeq);
        Assert.Null(result.FirstInvalidReason);
        Assert.Equal(3, result.LastSeq);
        Assert.NotNull(result.LastHash);
    }

    [Fact]
    public async Task ModifiedRow_IsReportedWithItsSequence()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        var store = new AuditEventStore(database);
        await RecordAsync(store, "first");
        await RecordAsync(store, "second");
        await RecordAsync(store, "third");

        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var tamper = connection.CreateCommand();
            tamper.CommandText = "UPDATE audit_events SET action='tampered' WHERE seq=2;";
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        var result = await store.VerifyChainAsync();

        Assert.False(result.Valid);
        Assert.Equal(2, result.FirstInvalidSeq);
        Assert.Equal("hash-mismatch", result.FirstInvalidReason);
    }

    [Fact]
    public async Task DeletedMiddleRow_BreaksTheLinkAtTheNextRow()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        var store = new AuditEventStore(database);
        await RecordAsync(store, "first");
        await RecordAsync(store, "second");
        await RecordAsync(store, "third");
        await RecordAsync(store, "fourth");

        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var tamper = connection.CreateCommand();
            tamper.CommandText = "DELETE FROM audit_events WHERE seq=3;";
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        var result = await store.VerifyChainAsync();

        Assert.False(result.Valid);
        Assert.Equal(4, result.FirstInvalidSeq);
        Assert.Equal("link-mismatch", result.FirstInvalidReason);
    }

    [Fact]
    public async Task VerificationEndpoint_RequiresAdministrator_AndDetectsTampering()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);

        var clean = await admin.GetFromJsonAsync<JsonElement>("/api/security/audit/verify");
        Assert.True(clean.GetProperty("valid").GetBoolean());
        Assert.True(clean.GetProperty("checked").GetInt64() >= 1);

        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var tamper = connection.CreateCommand();
            tamper.CommandText = "UPDATE audit_events SET path='/tampered-path' WHERE seq=1;";
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        var tampered = await admin.GetFromJsonAsync<JsonElement>("/api/security/audit/verify");
        Assert.False(tampered.GetProperty("valid").GetBoolean());
        Assert.Equal(1, tampered.GetProperty("firstInvalidSeq").GetInt64());
        Assert.Equal("hash-mismatch", tampered.GetProperty("firstInvalidReason").GetString());

        var create = await admin.PostAsJsonAsync("/api/security/users", new { username = "auditoperator", displayName = "Audit Operator", role = "Operator", password = "Operator!234" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var op = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/auth/login", new { username = "auditoperator", password = "Operator!234" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/security/audit/verify")).StatusCode);
    }

    private static Task RecordAsync(AuditEventStore store, string action)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = $"/test/{action}";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "Administrator")
        ], "test"));
        return store.RecordAsync(context, new AuditActionMetadata(action, "test"), StatusCodes.Status200OK);
    }

    private static async Task EnsureAdminAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        if (status.GetProperty("bootstrapRequired").GetBoolean())
        {
            var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username = "admin", displayName = "Plant Administrator", password = "AdminPass!234" });
            Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        }
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "AdminPass!234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}