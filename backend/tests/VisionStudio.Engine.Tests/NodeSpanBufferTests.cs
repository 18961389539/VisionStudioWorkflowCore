using System.Diagnostics;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 节点级 span 消费者（2026-11-12）。这组断言对着一个事实：只有埋点、没有消费者时，
/// <c>ActivitySource.StartActivity</c> 返回 null，产线上拿不到任何单次执行数据。
/// 因此这里验证的不是"能读到数据"，而是三条容易退化的性质：
/// 标签能落到记录上、失败/超时进得了失败清单、环形覆盖后 Overwritten 如实反映丢失。
/// </summary>
public sealed class NodeSpanBufferTests
{
    [Fact]
    public void CapturesTagsAndDurationFromEngineActivity()
    {
        using var source = NewSource();
        using var buffer = new NodeSpanBuffer(64, new NodeSpanSource(source));

        Emit(source, "node-1", "measure.caliper", runId: "run-9", scope: "module-a", sleepMs: 5);

        var snapshot = buffer.Snapshot();
        var record = Assert.Single(snapshot.Recent);
        Assert.Equal("node-1", record.NodeId);
        Assert.Equal("measure.caliper", record.NodeType);
        Assert.Equal("run-9", record.RunId);
        Assert.Equal("module-a", record.Scope);
        Assert.Equal("Ok", record.Status);
        Assert.False(record.TimedOut);
        Assert.True(record.DurationMs > 0, "Activity.Duration 必须转成毫秒真实耗时");
        Assert.Equal(1, snapshot.Captured);
        Assert.Equal(0, snapshot.Overwritten);
    }

    [Fact]
    public void FailuresAndTimeoutsLandInFailureList()
    {
        using var source = NewSource();
        using var buffer = new NodeSpanBuffer(64, new NodeSpanSource(source));

        Emit(source, "ok", "camera.grab");
        Emit(source, "timeout", "robot.move", timedOut: true);
        Emit(source, "failed", "image.threshold", fail: "port mismatch");

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Recent.Count);
        Assert.Equal(2, snapshot.Failures.Count);
        Assert.Contains(snapshot.Failures, r => r.NodeId == "timeout" && r.TimedOut);
        Assert.Contains(snapshot.Failures, r => r.NodeId == "failed" && r.Error == "port mismatch");
        // 最新在前：失败清单也要按时间倒序，否则现场排障先看到的是最旧的故障。
        Assert.Equal("failed", snapshot.Failures[0].NodeId);
    }

    [Fact]
    public void RingOverwritesOldestAndReportsIt()
    {
        using var source = NewSource();
        using var buffer = new NodeSpanBuffer(2, new NodeSpanSource(source));

        Emit(source, "first", "a");
        Emit(source, "second", "b");
        Emit(source, "third", "c");

        var snapshot = buffer.Snapshot();
        Assert.Equal(2, snapshot.Recent.Count);
        Assert.Equal(3, snapshot.Captured);
        // Overwritten 是"Recent 不是全量"的唯一信号，算错会让诊断包把残缺当完整。
        Assert.Equal(1, snapshot.Overwritten);
        Assert.DoesNotContain(snapshot.Recent, r => r.NodeId == "first");
        Assert.Equal("third", snapshot.Recent[0].NodeId);
    }

    [Fact]
    public void DisposeStopsCapture()
    {
        using var source = NewSource();
        var buffer = new NodeSpanBuffer(8, new NodeSpanSource(source));
        buffer.Dispose();

        // 监听器已摘除：ActivitySource 回到"无监听者返回 null"的状态，这是最直接的证据；
        // 同时确认 Dispose 之后的写入不会因为空引用或已释放的锁抛异常。
        Assert.Null(source.StartActivity("node.execute"));
        Emit(source, "after-dispose", "a");
        Assert.Empty(buffer.Snapshot().Recent);
    }

    [Fact]
    public void IgnoresActivitiesFromOtherSources()
    {
        using var source = NewSource();
        using var foreign = new ActivitySource($"VisionStudio.Tests.Foreign.{Guid.NewGuid():N}");
        // 给外来 source 自己配一个监听者：否则 StartActivity 返回 null，这条用例就成了空转。
        using var foreignListener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, foreign),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(foreignListener);
        using var buffer = new NodeSpanBuffer(8, new NodeSpanSource(source));

        using (var activity = foreign.StartActivity("Microsoft.AspNetCore.Hosting.HttpRequestIn"))
        {
            Assert.NotNull(activity);
            activity.SetTag("vision.node.id", "must-not-be-captured");
        }

        // 真后端冒烟里出现过 HTTP 请求 span 混进"最近节点执行"：这里把"只收本 Source"钉死。
        Assert.Empty(buffer.Snapshot().Recent);
    }

    private static ActivitySource NewSource() => new($"VisionStudio.Tests.NodeSpans.{Guid.NewGuid():N}");

    private static void Emit(
        ActivitySource source,
        string nodeId,
        string nodeType,
        string? runId = null,
        string? scope = null,
        bool timedOut = false,
        string? fail = null,
        int sleepMs = 0)
    {
        using var activity = source.StartActivity("node.execute");
        // 无监听者时 StartActivity 返回 null：Dispose 之后的用例靠这条路径验证监听器已摘除。
        if (activity is null) return;
        activity.SetTag("vision.node.id", nodeId);
        activity.SetTag("vision.node.type", nodeType);
        if (runId is not null) activity.SetTag("vision.run.id", runId);
        if (scope is not null) activity.SetTag("vision.scope", scope);
        if (timedOut) activity.SetTag("vision.node.timed_out", true);
        if (fail is not null) activity.SetStatus(ActivityStatusCode.Error, fail);
        if (sleepMs > 0) Thread.Sleep(sleepMs);
    }
}
