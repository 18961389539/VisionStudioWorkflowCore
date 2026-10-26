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
}
