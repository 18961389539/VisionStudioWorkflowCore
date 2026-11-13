using Microsoft.Extensions.Options;
using VisionStudio.Api;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 机器人命令追踪的保留策略（2026-11-10）：此前按命令逐条落盘且只增不减。
/// 契约：过期文件按天删除；超过文件数上限时从最旧开始删；24 小时内文件永不按数量删除。
/// </summary>
public sealed class RobotTraceRetentionTests
{
    private static RobotCommandTraceStore Store(string root, RobotTraceRetentionOptions options)
        => new(root, Options.Create(options));

    private static string WriteTrace(string root, DateTimeOffset lastWrite, string traceId)
    {
        var directory = Path.Combine(root, lastWrite.UtcDateTime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, traceId + ".json");
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    [Fact]
    public async Task Cleanup_DeletesExpiredTraces_AndRemovesEmptyDayDirectory()
    {
        using var env = new TempWebHostEnvironment();
        var root = Path.Combine(env.ContentRootPath, "robot-traces");
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var old = WriteTrace(root, now.AddDays(-31), "old");
        var recent = WriteTrace(root, now.AddDays(-29), "recent");
        var store = Store(root, new RobotTraceRetentionOptions { RetentionDays = 30, MaxTraceFiles = 0 });

        var deleted = await store.CleanupAsync(now, default);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Cleanup_CapsFileCount_DeletingOldestFirst()
    {
        using var env = new TempWebHostEnvironment();
        var root = Path.Combine(env.ContentRootPath, "robot-traces");
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var oldest = WriteTrace(root, now.AddDays(-3), "oldest");
        var middle = WriteTrace(root, now.AddDays(-2), "middle");
        var newest = WriteTrace(root, now.AddHours(-25), "newest");
        var store = Store(root, new RobotTraceRetentionOptions { RetentionDays = 30, MaxTraceFiles = 2 });

        var deleted = await store.CleanupAsync(now, default);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public async Task Cleanup_ProtectsFilesYoungerThanOneDay_FromTheFileCountCap()
    {
        using var env = new TempWebHostEnvironment();
        var root = Path.Combine(env.ContentRootPath, "robot-traces");
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var twoHours = WriteTrace(root, now.AddHours(-2), "two-hours");
        var oneHour = WriteTrace(root, now.AddHours(-1), "one-hour");
        var minutes = WriteTrace(root, now.AddMinutes(-5), "minutes");
        var store = Store(root, new RobotTraceRetentionOptions { RetentionDays = 30, MaxTraceFiles = 1 });

        var deleted = await store.CleanupAsync(now, default);

        // 宁可暂时超过上限，也不删正在排查中的证据。
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(twoHours));
        Assert.True(File.Exists(oneHour));
        Assert.True(File.Exists(minutes));
    }

    [Fact]
    public void SelectForDeletion_WithRetentionDisabled_KeepsEverything()
    {
        var files = new[] { ("a.json", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), 1L) };

        var doomed = RobotCommandTraceStore.SelectForDeletion(
            files, new RobotTraceRetentionOptions { RetentionDays = 0, MaxTraceFiles = 0 }, DateTimeOffset.UtcNow);

        Assert.Empty(doomed);
    }

    [Fact]
    public async Task Cleanup_IsIdempotent_AndKeepsTraceFilesReadable()
    {
        using var env = new TempWebHostEnvironment();
        var root = Path.Combine(env.ContentRootPath, "robot-traces");
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var kept = WriteTrace(root, now.AddDays(-1), "kept");
        var store = Store(root, new RobotTraceRetentionOptions { RetentionDays = 30, MaxTraceFiles = 10 });

        Assert.Equal(0, await store.CleanupAsync(now, default));
        Assert.Equal(0, await store.CleanupAsync(now, default));
        Assert.True(File.Exists(kept));
    }
}
