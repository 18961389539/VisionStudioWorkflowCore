using System.Diagnostics;

namespace VisionStudio.Engine.Device;

internal sealed class ProtocolDiagnosticsCounter
{
    private long _transactions;
    private long _batchReads;
    private long _batchWrites;
    private long _roundTripTicks;
    private long _lastRoundTripTicks;
    private long _lastTransactionUnixMs;
    private string? _lastError;

    public async Task<T> TrackAsync<T>(Func<Task<T>> action, bool batchRead = false, bool batchWrite = false)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await action();
            RecordSuccess(started, batchRead, batchWrite);
            return value;
        }
        catch (Exception ex)
        {
            RecordFailure(started, ex, batchRead, batchWrite);
            throw;
        }
    }

    public async Task TrackAsync(Func<Task> action, bool batchRead = false, bool batchWrite = false)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action();
            RecordSuccess(started, batchRead, batchWrite);
        }
        catch (Exception ex)
        {
            RecordFailure(started, ex, batchRead, batchWrite);
            throw;
        }
    }

    public DeviceProtocolDiagnostics Snapshot()
    {
        var tx = Interlocked.Read(ref _transactions);
        var totalTicks = Interlocked.Read(ref _roundTripTicks);
        var lastTicks = Interlocked.Read(ref _lastRoundTripTicks);
        var lastUnix = Interlocked.Read(ref _lastTransactionUnixMs);
        return new DeviceProtocolDiagnostics(
            tx,
            Interlocked.Read(ref _batchReads),
            Interlocked.Read(ref _batchWrites),
            StopwatchTicksToMs(lastTicks),
            tx == 0 ? 0 : StopwatchTicksToMs(totalTicks) / tx,
            lastUnix <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastUnix),
            Volatile.Read(ref _lastError));
    }

    private void RecordSuccess(long started, bool batchRead, bool batchWrite)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        Interlocked.Increment(ref _transactions);
        Interlocked.Add(ref _roundTripTicks, elapsed);
        Interlocked.Exchange(ref _lastRoundTripTicks, elapsed);
        Interlocked.Exchange(ref _lastTransactionUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (batchRead) Interlocked.Increment(ref _batchReads);
        if (batchWrite) Interlocked.Increment(ref _batchWrites);
        Volatile.Write(ref _lastError, null);
    }

    private void RecordFailure(long started, Exception ex, bool batchRead, bool batchWrite)
    {
        RecordSuccess(started, batchRead, batchWrite);
        Volatile.Write(ref _lastError, ex.Message);
    }

    private static double StopwatchTicksToMs(long ticks)
        => ticks <= 0 ? 0 : ticks * 1000d / Stopwatch.Frequency;
}
