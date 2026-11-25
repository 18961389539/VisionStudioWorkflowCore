using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api;
using VisionStudio.Api.Security;
using VisionStudio.Api.Storage.EfCore;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 服务账号令牌（P3-6）：MES/上位机用独立令牌而不是共用操作员账号。
/// 重点守住：库内只有哈希、作用域默认拒绝、轮换/吊销立即生效、令牌调用可审计。
/// </summary>
public sealed class ServiceTokenTests
{
    [Fact]
    public async Task ConcurrentStaleTokenReads_IssueOnlyOneUsageUpdate()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        var probe = new UsageWriteProbe();
        using var store = new ServiceTokenStore(new UsageProbeFactory(database, probe));
        var created = await store.CreateAsync(new CreateServiceTokenRequest("burst-reader", ["results:read"], null), "admin");
        probe.SynchronizeReaders(32);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var principals = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => store.ResolveAsync(created.Token, timeout.Token)));
        Assert.All(principals, principal => Assert.Equal(created.Details.Id, principal?.Id));
        Assert.Equal(1, probe.Updates);
        Assert.NotNull(await store.ResolveAsync(created.Token));
        Assert.Equal(1, probe.Updates);
    }

    [Fact]
    public async Task ServiceToken_IsStoredHashed_AndPlaintextIsReturnedOnlyOnce()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/security/service-tokens", new { name = "mes-line1", scopes = new[] { "results:read" }, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("token").GetString()!;
        Assert.StartsWith("vst_", secret, StringComparison.Ordinal);
        var details = created.GetProperty("details");
        Assert.Equal("mes-line1", details.GetProperty("name").GetString());
        Assert.Equal(secret[..12], details.GetProperty("prefix").GetString());
        var tokenId = details.GetProperty("id").GetString()!;

        // 列表只暴露前缀/作用域/时间，不返回明文也不返回哈希。
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/security/service-tokens");
        Assert.DoesNotContain(secret, list.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("id").GetString() == tokenId);

        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM security_service_tokens WHERE id=$id;";
        command.Parameters.AddWithValue("$id", tokenId);
        var stored = (string?)await command.ExecuteScalarAsync();
        Assert.NotEqual(secret, stored);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), stored);
    }

    [Fact]
    public async Task ResultsReadToken_ReadsTraces_ButCannotTouchOtherApis_AndItsUseIsAudited()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var secret = await CreateTokenAsync(admin, "mes-reader", "results:read");

        using var client = factory.CreateFreshClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/traces?take=5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/traces/stats")).StatusCode);
        // 兜底策略不让服务主体通过：未显式标注作用域的接口一律 403。
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/security/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/run", EmptyWorkflow())).StatusCode);

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/audit?action=trace.read&limit=50");
        Assert.Contains(audit.EnumerateArray(), item =>
            item.GetProperty("username").GetString() == "service:mes-reader" &&
            item.GetProperty("role").GetString() == "Service" &&
            item.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RunTriggerToken_CanTriggerRuns_ButCannotReadTraces_AndTheRunIsAudited()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var secret = await CreateTokenAsync(admin, "mes-runner", "runs:run");

        using var client = factory.CreateFreshClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/traces?take=5")).StatusCode);

        var run = await client.PostAsJsonAsync("/api/run", EmptyWorkflow());
        Assert.True(run.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden,
            $"runs:run token should pass authorization, got {(int)run.StatusCode}");

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/audit?action=workflow.run.adhoc&limit=50");
        Assert.Contains(audit.EnumerateArray(), item => item.GetProperty("username").GetString() == "service:mes-runner");
    }

    [Fact]
    public async Task RotationReplacesTheSecret_AndRevocationStopsTheToken()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);
        var original = await CreateTokenAsync(admin, "mes-rotate", "results:read");

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/security/service-tokens");
        var tokenId = list.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "mes-rotate").GetProperty("id").GetString()!;

        var rotatedResponse = await admin.PostAsync($"/api/security/service-tokens/{tokenId}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var replacement = rotated.GetProperty("token").GetString()!;
        Assert.NotEqual(original, replacement);
        Assert.Equal(tokenId, rotated.GetProperty("details").GetProperty("id").GetString());

        using var stale = CreateBearerClient(factory, original);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/traces?take=5")).StatusCode);

        using var current = CreateBearerClient(factory, replacement);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/traces?take=5")).StatusCode);

        var revoked = await admin.PostAsync($"/api/security/service-tokens/{tokenId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await current.GetAsync("/api/traces?take=5")).StatusCode);

        var after = await admin.GetFromJsonAsync<JsonElement>("/api/security/service-tokens");
        var row = after.EnumerateArray().Single(x => x.GetProperty("id").GetString() == tokenId);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("revokedAt").ValueKind);

        // 已吊销令牌不能轮换，避免"吊销后又复活"。
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/security/service-tokens/{tokenId}/rotate", null)).StatusCode);
    }

    [Fact]
    public async Task OnlyAdministratorsCanManageServiceTokens_AndUnknownScopesAreRejected()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var admin = factory.CreateFreshClient();
        await EnsureAdminAsync(admin);

        var create = await admin.PostAsJsonAsync("/api/security/users", new { username = "lineoperator", displayName = "Line Operator", role = "Operator", password = "Operator!234" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var op = factory.CreateFreshClient();
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/auth/login", new { username = "lineoperator", password = "Operator!234" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/security/service-tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/security/service-tokens", new { name = "op-token", scopes = new[] { "results:read" } })).StatusCode);

        var badScope = await admin.PostAsJsonAsync("/api/security/service-tokens", new { name = "bad-scope", scopes = new[] { "admin:all" } });
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
    }

    private static HttpClient CreateBearerClient(SecureVisionStudioApiFactory factory, string token)
    {
        var client = factory.CreateFreshClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object EmptyWorkflow() => new { id = "service-token-run", name = "Service Token Run", nodes = Array.Empty<object>(), edges = Array.Empty<object>() };

    private static async Task<string> CreateTokenAsync(HttpClient admin, string name, string scope)
    {
        var response = await admin.PostAsJsonAsync("/api/security/service-tokens", new { name, scopes = new[] { scope } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
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

    private sealed class UsageWriteProbe : DbCommandInterceptor
    {
        private int _remaining;
        private int _updates;
        private TaskCompletionSource _readers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Updates => Volatile.Read(ref _updates);
        public void SynchronizeReaders(int count) => _remaining = count;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _remaining) > 0 && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"prefix\"", StringComparison.Ordinal))
            {
                if (Interlocked.Decrement(ref _remaining) == 0) _readers.TrySetResult();
                await _readers.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"security_service_tokens\"", StringComparison.Ordinal))
                Interlocked.Increment(ref _updates);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class UsageProbeFactory(SqliteMetadataDatabase database, UsageWriteProbe probe) : IVisionStudioDbFactory
    {
        public SqliteMetadataDatabase Database => database;
        public async Task<VisionStudioDbContext> CreateDbContextAsync(CancellationToken ct = default, bool strongDurability = false)
        {
            var connection = await database.OpenConnectionAsync(ct, strongDurability);
            return new VisionStudioDbContext(new DbContextOptionsBuilder<VisionStudioDbContext>()
                .UseSqlite(connection, contextOwnsConnection: true)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(probe).Options);
        }
        public VisionStudioDbContext CreateDbContext() => new VisionStudioDbFactory(database).CreateDbContext();
        public Task<ImmediateDbTransaction> BeginImmediateTransactionAsync(VisionStudioDbContext db, CancellationToken ct = default) =>
            db.BeginImmediateTransactionAsync(ct);
    }
}