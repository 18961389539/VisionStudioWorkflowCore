using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 报警存储的保留策略与分页（2026-10-26）。
/// 此前 alarms.json 只增不减、`GET /api/alarms` 返回全量：跑久了文件膨胀、接口一次拉满。
/// 这里守住两条契约：活动报警永不裁剪；列表按 skip/take 分页且返回裁剪前总数。
/// </summary>
public sealed class AlarmRetentionTests
{
    private static AlarmRecord Alarm(string code, bool active, int minuteOffset) => new(
        Id: Guid.NewGuid().ToString("N"),
        Code: code,
        Severity: AlarmSeverity.Warning,
        Source: "test",
        Message: code,
        Active: active,
        Acknowledged: false,
        RaisedAt: new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero).AddMinutes(minuteOffset));

    [Fact]
    public void Trim_KeepsAllActive_AndNewestInactiveUpToLimit()
    {
        var items = new List<AlarmRecord>
        {
            Alarm("A1", true, 1), Alarm("A2", true, 2), Alarm("A3", true, 3),
            Alarm("I1", false, 1), Alarm("I2", false, 2), Alarm("I3", false, 3), Alarm("I4", false, 4), Alarm("I5", false, 5)
        };

        var kept = AlarmStore.TrimToRetention(items, maxRetained: 4);

        Assert.Equal(4, kept.Count);
        Assert.All(kept.Where(x => x.Active), _ => { });
        Assert.Equal(3, kept.Count(x => x.Active)); // 活动报警一条不丢
        // 只剩 1 个名额给已恢复报警，按时间保留最新的 I5
        Assert.Contains(kept, x => x.Code == "I5");
        Assert.DoesNotContain(kept, x => x.Code == "I4");
    }

    [Fact]
    public void Trim_KeepsEverythingBelowLimit()
    {
        var items = new[] { Alarm("A1", true, 1), Alarm("I1", false, 2) };
        Assert.Equal(2, AlarmStore.TrimToRetention(items, 10).Count);
    }

    [Fact]
    public void Trim_DoesNotDropActiveEvenWhenTheyExceedLimit()
    {
        // 极端场景：现场同时挂着大量未恢复报警。宁可超过上限，也不能把活动报警裁掉。
        var items = Enumerable.Range(0, 5).Select(i => Alarm($"A{i}", true, i)).ToList();
        Assert.Equal(5, AlarmStore.TrimToRetention(items, maxRetained: 2).Count);
    }

    [Fact]
    public async Task Raise_AppliesRetention_AndListPagesWithTotal()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);
        for (var i = 0; i < 5; i++)
            await store.RaiseAsync($"CODE-{i}", AlarmSeverity.Warning, "test", $"alarm {i}", null, default);

        var page = await store.ListAsync(activeOnly: false, skip: 1, take: 2, default);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.Total); // 总数是分页前的
        // 时间倒序：skip 1 后应是第 2、3 新的记录
        Assert.Equal("CODE-3", page.Items[0].Code);
        Assert.Equal("CODE-2", page.Items[1].Code);
    }

    [Fact]
    public async Task List_ClampsTakeAndTreatsNegativeSkipAsZero()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);
        for (var i = 0; i < 3; i++)
            await store.RaiseAsync($"CODE-{i}", AlarmSeverity.Warning, "test", $"alarm {i}", null, default);

        var capped = await store.ListAsync(false, skip: -5, take: int.MaxValue, default);
        Assert.Equal(3, capped.Items.Count); // 不超过实际条数；take 被夹到上限
        Assert.Equal(3, capped.Total);
    }

    [Fact]
    public async Task Raise_PreservesActiveAlarmIdentityAndAcknowledgementUntilRecovery()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);

        var first = await store.RaiseAsync("SAME", AlarmSeverity.Warning, "src", "first", null, default);
        var confirmed = await store.AcknowledgeAsync(first.Id, default);
        var second = await store.RaiseAsync("SAME", AlarmSeverity.Critical, "src", "second", "run-1", default);

        var page = await store.ListAsync(false, 0, 10, default);
        Assert.Equal(1, page.Total);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.RaisedAt, second.RaisedAt);
        Assert.True(second.LastRaisedAt >= first.RaisedAt);
        Assert.Equal(2, second.OccurrenceCount);
        Assert.True(second.Acknowledged);
        Assert.Equal(confirmed.AcknowledgedAt, second.AcknowledgedAt);
        Assert.Equal(second.Id, page.Items[0].Id);
        Assert.Equal(AlarmSeverity.Critical, page.Items[0].Severity);
        Assert.Equal("run-1", page.Items[0].RunId);

        await store.RecoverAsync("SAME", "src", default);
        var recurring = await store.RaiseAsync("SAME", AlarmSeverity.Warning, "src", "third", null, default);
        Assert.NotEqual(first.Id, recurring.Id);
        Assert.False(recurring.Acknowledged);
        Assert.Equal(1, recurring.OccurrenceCount);
        Assert.Equal(2, (await store.ListAsync(false, 0, 10, default)).Total);
    }

    [Fact]
    public async Task StationScopedAlarmHistoryAndSummary_OnlyIncludeThatStation()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);
        await store.RaiseAsync("A", AlarmSeverity.Critical, "ProductionRuntime:line-a", "line a alarm", null, default);
        await store.RaiseAsync("B", AlarmSeverity.Warning, "ProductionRuntime:line-b", "line b alarm", null, default);

        var page = await store.ListAsync(false, 0, 10, default, source: "ProductionRuntime:line-a");
        var summary = await store.GetSummaryAsync(default, "ProductionRuntime:line-a");

        Assert.Single(page.Items);
        Assert.Equal("line a alarm", page.Items[0].Message);
        Assert.Equal(1, page.Total);
        Assert.Equal(new AlarmSummary(1, 1, "line a alarm", 1), summary);
    }

    [Fact]
    public async Task RevisionStamp_ChangesOnRaiseAcknowledgeAndRecover()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);

        var initial = store.RevisionStamp();
        var alarm = await store.RaiseAsync("R-1", AlarmSeverity.Warning, "src", "message", null, default);
        var afterRaise = store.RevisionStamp();
        Assert.NotEqual(initial, afterRaise);

        await store.AcknowledgeAsync(alarm.Id, default);
        var afterAcknowledge = store.RevisionStamp();
        Assert.NotEqual(afterRaise, afterAcknowledge);

        await store.RecoverAsync("R-1", "src", default);
        Assert.NotEqual(afterAcknowledge, store.RevisionStamp());
    }

    [Fact]
    public async Task LegacyJson_IsImportedAndArchivedOnFirstUse()
    {
        using var env = new TempWebHostEnvironment();
        var production = Path.Combine(env.ContentRootPath, "data", "production");
        Directory.CreateDirectory(production);
        var legacy = Path.Combine(production, "alarms.json");
        await File.WriteAllTextAsync(legacy, """
[
  {
    "id": "legacy-1",
    "code": "LEGACY-ACTIVE",
    "severity": "Critical",
    "source": "legacy",
    "message": "active alarm",
    "active": true,
    "acknowledged": false,
    "raisedAt": "2026-10-26T08:00:00+00:00"
  },
  {
    "id": "legacy-2",
    "code": "LEGACY-RECOVERED",
    "severity": "Warning",
    "source": "legacy",
    "message": "recovered alarm",
    "active": false,
    "acknowledged": true,
    "raisedAt": "2026-10-26T07:00:00+00:00",
    "recoveredAt": "2026-10-26T07:30:00+00:00",
    "acknowledgedAt": "2026-10-26T07:10:00+00:00"
  }
]
""");

        var store = new AlarmStore(env);
        var page = await store.ListAsync(false, 0, 10, default);

        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, x => x.Code == "LEGACY-ACTIVE" && x.Active);
        Assert.Contains(page.Items, x => x.Code == "LEGACY-RECOVERED" && !x.Active && x.Acknowledged);
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(production, "alarms.json.migrated-*"));

        // 归档后再次构造不会重复导入。
        var reopened = new AlarmStore(env);
        Assert.Equal(2, (await reopened.ListAsync(false, 0, 10, default)).Total);
    }

    [Fact]
    public async Task SummaryIncludesCriticalAlarmOutsideCurrentHistoryPage()
    {
        using var env = new TempWebHostEnvironment();
        var critical = Alarm("OLD-CRITICAL", true, 0) with { Severity = AlarmSeverity.Critical };
        var history = Enumerable.Range(1, 205).Select(i => Alarm($"RECOVERED-{i}", false, i));
        await WriteLegacyAsync(env, history.Append(critical));
        var store = new AlarmStore(env);

        var recent = await store.ListAsync(false, 0, 200, default);
        Assert.Equal(206, recent.Total);
        Assert.DoesNotContain(recent.Items, alarm => alarm.Active);
        var older = await store.ListAsync(false, 200, 8, default);
        Assert.Equal(critical.Id, older.Items[^1].Id);
        Assert.Equal(new AlarmSummary(1, 1, "OLD-CRITICAL", 1), await store.GetSummaryAsync(default));

        await store.RecoverAsync(critical.Code, critical.Source, default);
        Assert.Equal(new AlarmSummary(0, 0, null, 0), await store.GetSummaryAsync(default));
    }

    [Fact]
    public async Task PendingFilterPaginatesOnlyActiveUnacknowledgedAlarms()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<AlarmStore>();
        var first = await store.RaiseAsync("PENDING2", AlarmSeverity.Critical, "test", "pending", null, default);
        var second = await store.RaiseAsync("PENDING1", AlarmSeverity.Warning, "test", "pending", null, default);
        var acknowledged = await store.RaiseAsync("ACK", AlarmSeverity.Warning, "test", "ack", null, default);
        await store.AcknowledgeAsync(acknowledged.Id, default);
        await store.RaiseAsync("RECOVERED", AlarmSeverity.Warning, "test", "recovered", null, default);
        await store.RecoverAsync("RECOVERED", "test", default);

        using var page1 = await client.GetAsync("/api/alarms?pendingOnly=true&take=1");
        using var page2 = await client.GetAsync("/api/alarms?pendingOnly=true&skip=1&take=1");
        var pendingIds = new List<string>();
        foreach (var page in new[] { page1, page2 })
        {
            page.EnsureSuccessStatusCode();
            Assert.Equal("2", page.Headers.GetValues("X-Total-Count").Single());
            var data = await page.Content.ReadFromJsonAsync<JsonElement>();
            pendingIds.Add(Assert.Single(data.EnumerateArray()).GetProperty("id").GetString()!);
        }
        Assert.Equal(new[] { first.Id, second.Id }.Order(), pendingIds.Order());

        using var ack = await client.PostAsync($"/api/alarms/{first.Id}/ack", null);
        ack.EnsureSuccessStatusCode();
        var remaining = await client.GetFromJsonAsync<JsonElement>("/api/alarms?pendingOnly=true");
        Assert.Equal(second.Id, Assert.Single(remaining.EnumerateArray()).GetProperty("id").GetString());
        var active = await client.GetFromJsonAsync<JsonElement>("/api/alarms?activeOnly=true");
        Assert.Equal(3, active.GetArrayLength());
        var summary = await client.GetFromJsonAsync<AlarmSummary>("/api/alarms/summary");
        Assert.Equal(1, summary!.PendingCount);
        Assert.Equal(3, summary.ActiveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyJson_ArchiveRetryDoesNotReplayCommittedImport(bool reopen)
    {
        using var env = new TempWebHostEnvironment();
        var legacy = Alarm("LEGACY", true, 1);
        var path = await WriteLegacyAsync(env, [legacy]);
        var archiveBlocked = true;
        var store = new AlarmStore(new SqliteMetadataDatabase(env), env, NullLogger<AlarmStore>.Instance, (source, destination) =>
        {
            if (archiveBlocked) throw new IOException("Simulated archive failure after database commit.");
            File.Move(source, destination);
        });

        Assert.Single((await store.ListAsync(false, 0, 10, default)).Items);
        Assert.True(File.Exists(path));
        var confirmed = await store.AcknowledgeAsync(legacy.Id, default);
        Assert.True(confirmed.Acknowledged);
        await store.RecoverAsync(legacy.Code, legacy.Source, default);
        var current = await store.RaiseAsync(legacy.Code, AlarmSeverity.Error, legacy.Source, "new alarm", null, default);
        Assert.True(File.Exists(path));

        archiveBlocked = false;
        var retry = reopen ? new AlarmStore(env) : store;
        var page = await retry.ListAsync(false, 0, 10, default);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, alarm => alarm.Id == legacy.Id && alarm.Acknowledged && !alarm.Active);
        Assert.Contains(page.Items, alarm => alarm.Id == current.Id && alarm.Active);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "alarms.json.migrated-*"));
    }

    [Fact]
    public async Task LegacyJson_CancelledRequestCanRetryOnSameStore()
    {
        using var env = new TempWebHostEnvironment();
        var path = await WriteLegacyAsync(env, [Alarm("LEGACY", true, 1)]);
        var store = new AlarmStore(env);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListAsync(false, 0, 10, new CancellationToken(true)));
        Assert.True(File.Exists(path));
        Assert.Single((await store.ListAsync(false, 0, 10, default)).Items);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LegacyJson_PreservesExistingSqliteRowsWithoutMarker()
    {
        using var env = new TempWebHostEnvironment();
        var store = new AlarmStore(env);
        var current = await store.RaiseAsync("EXISTING", AlarmSeverity.Critical, "test", "current alarm", null, default);
        await store.AcknowledgeAsync(current.Id, default);
        var newerLegacy = Alarm(current.Code, true, 2);
        var olderLegacy = Alarm(current.Code, true, 1) with { Id = current.Id };
        await WriteLegacyAsync(env, [newerLegacy, olderLegacy, Alarm("HISTORY", false, 0)]);

        var page = await new AlarmStore(env).ListAsync(false, 0, 10, default);
        Assert.Equal(2, page.Total);
        var active = Assert.Single(page.Items, alarm => alarm.Active);
        Assert.Equal(current.Id, active.Id);
        Assert.True(active.Acknowledged);
        Assert.Equal("current alarm", active.Message);
    }

    [Fact]
    public async Task LegacyJson_FailedImportRollsBackRowsAndCompletionMarker()
    {
        using var env = new TempWebHostEnvironment();
        var valid = Alarm("VALID", true, 2);
        var invalid = Alarm("INVALID", true, 1) with { Code = null! };
        var path = await WriteLegacyAsync(env, [valid, invalid]);
        var store = new AlarmStore(env);

        Assert.Empty((await store.ListAsync(false, 0, 10, default)).Items);
        Assert.True(File.Exists(path));
        await WriteLegacyAsync(env, [valid]);
        Assert.Equal(valid.Id, Assert.Single((await store.ListAsync(false, 0, 10, default)).Items).Id);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WindowsV30ChecksumOpensAndUpgradesExistingAlarmRows()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        var store = new AlarmStore(env);
        var initial = await store.RaiseAsync("V30", AlarmSeverity.Warning, "test", "older alarm", null, default);
        await store.AcknowledgeAsync(initial.Id, default);

        await using var connection = await database.OpenConnectionAsync();
        await using var simulateV30 = connection.CreateCommand();
        simulateV30.CommandText = """
ALTER TABLE alarms DROP COLUMN last_raised_at;
ALTER TABLE alarms DROP COLUMN occurrence_count;
DELETE FROM schema_migration_history WHERE version > 30;
UPDATE schema_info SET version = 30;
UPDATE schema_migration_history SET checksum = '95321f038626ac8fb3778ea110fe33f59c3ae4c5b1828fe3952cb7720fe44e91' WHERE version = 30;
""";
        await simulateV30.ExecuteNonQueryAsync();

        var reopened = new AlarmStore(env);
        var alarm = Assert.Single((await reopened.ListAsync(false, 0, 10, default)).Items);
        Assert.Equal(initial.Id, alarm.Id);
        Assert.True(alarm.Acknowledged);
        Assert.Equal(alarm.RaisedAt, alarm.LastRaisedAt);
        Assert.Equal(1, alarm.OccurrenceCount);
        Assert.Equal(SqliteSchemaMigrationRunner.CurrentVersion, (await new SqliteSchemaMigrationRunner().MigrateAsync(connection)).CurrentVersion);
    }

    private static async Task<string> WriteLegacyAsync(TempWebHostEnvironment env, IEnumerable<AlarmRecord> alarms)
    {
        var production = Path.Combine(env.ContentRootPath, "data", "production");
        Directory.CreateDirectory(production);
        var path = Path.Combine(production, "alarms.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(alarms, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }
}
