using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Tests;

public sealed class CameraSdkDiagnosticsTests
{
    [Fact]
    public void RecordFailure_AggregatesByDriverAndOperation()
    {
        var driver = "test-" + Guid.NewGuid().ToString("N");

        CameraSdkDiagnostics.RecordFailure(driver, "close", new InvalidOperationException("first"));
        CameraSdkDiagnostics.RecordFailure(driver, "close", new InvalidOperationException("second"));
        CameraSdkDiagnostics.RecordFailure(driver, "stop-grabbing", new InvalidOperationException("third"));

        var entries = CameraSdkDiagnostics.Snapshot().Where(x => x.Driver == driver).ToArray();

        Assert.Equal(2, entries.Length);
        var close = entries.Single(x => x.Operation == "close");
        Assert.Equal(2, close.Failures);
        Assert.Equal("second", close.LastError);
        Assert.NotNull(close.LastFailureAt);
        var stop = entries.Single(x => x.Operation == "stop-grabbing");
        Assert.Equal(1, stop.Failures);
    }

    [Fact]
    public void RecordFailure_IgnoresBlankIdentifiers()
    {
        var driver = "test-" + Guid.NewGuid().ToString("N");

        CameraSdkDiagnostics.RecordFailure(" ", "close", new InvalidOperationException("ignored"));
        CameraSdkDiagnostics.RecordFailure(driver, " ", new InvalidOperationException("ignored"));

        var snapshot = CameraSdkDiagnostics.Snapshot();
        Assert.DoesNotContain(snapshot, x => x.Driver == " ");
        Assert.DoesNotContain(snapshot, x => x.Driver == driver);
    }
}
