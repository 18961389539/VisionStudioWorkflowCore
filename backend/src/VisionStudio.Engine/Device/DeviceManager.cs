using System.Collections.Concurrent;
using System.Diagnostics;

namespace VisionStudio.Engine.Device;

/// <summary>
/// Owns device lifecycle, polling, cached tag values, heartbeat and reconnect policy.
/// Workflow nodes consume this service rather than vendor/protocol SDK objects.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private sealed class Entry(IDeviceDriver driver)
    {
        // Command-level mutual exclusion: serializes writes (single, batch and ordered sequences)
        // so a multi-call PLC command cannot interleave with another run's writes.
        public readonly SemaphoreSlim CommandGate = new(1, 1);
        public readonly SemaphoreSlim LifecycleGate = new(1, 1);
        public readonly ConcurrentDictionary<string, DeviceTagSample> Values = new(StringComparer.OrdinalIgnoreCase);
        public IDeviceDriver Driver { get; } = driver;
        public DeviceRuntimeSettings Settings { get; set; } = new();
        public CancellationTokenSource? PollCts { get; set; }
        public Task? PollTask { get; set; }
        public long PollCycles;
        public long BatchReadCycles;
        public long ReadErrors;
        public long WriteErrors;
        public long ReconnectCount;
        public DateTimeOffset? LastPollAt;
        public DateTimeOffset? LastGoodReadAt;
        public DateTimeOffset? LastDeviceHeartbeatAt;
        public DateTimeOffset PollStartedAt = DateTimeOffset.UtcNow;
        public bool HostHeartbeat;
        public bool DeviceHeartbeat;
        public string? RuntimeError;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IDeviceDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (!_entries.TryAdd(driver.Id, new Entry(driver)))
            throw new InvalidOperationException($"Device '{driver.Id}' is already registered.");
    }

    public IReadOnlyList<DeviceDescriptor> List()
        => _entries.Values.Select(Snapshot).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public DeviceDescriptor Get(string id) => Snapshot(RequireEntry(id));

    /// <summary>
    /// R02：该设备当前是否有在途命令（命令门被占用）——租约接管的空闲判定依据。
    /// 租约 TTL 到期只说明持有者可能失联，不能作为"设备可用"的证据。
    /// </summary>
    public bool IsCommandInFlight(string id) => RequireEntry(id).CommandGate.CurrentCount == 0;
    public IDeviceDriver Require(string id) => RequireEntry(id).Driver;

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryRemove(id, out var entry)) return false;
        await entry.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopPollingAsync(entry);
            try { await entry.Driver.DisconnectAsync(cancellationToken); } catch { }
            await entry.Driver.DisposeAsync();
            return true;
        }
        finally
        {
            entry.LifecycleGate.Release();
            entry.LifecycleGate.Dispose();
            entry.CommandGate.Dispose();
        }
    }

    public async Task<IReadOnlyList<DeviceTagSample>> ReadAllFreshAsync(string deviceId, bool autoConnect = true, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(deviceId);
        await EnsureConnectedAsync(entry, autoConnect, cancellationToken);
        if (entry.Driver is IDeviceBatchDriver batch && entry.Driver.Tags.Count > 0)
        {
            try
            {
                var samples = await batch.ReadManyAsync(entry.Driver.Tags.Select(x => x.Id).ToArray(), cancellationToken);
                foreach (var sample in samples) UpdateSample(entry, sample);
                Interlocked.Increment(ref entry.BatchReadCycles);
                return samples;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref entry.ReadErrors);
                entry.RuntimeError = ex.Message;
                throw;
            }
        }

        var output = new List<DeviceTagSample>(entry.Driver.Tags.Count);
        foreach (var tag in entry.Driver.Tags)
            output.Add(await ReadTagAsync(deviceId, tag.Id, fresh: true, autoConnect: autoConnect, cancellationToken: cancellationToken));
        return output;
    }

    public async Task WriteManyAsync(string deviceId, IReadOnlyDictionary<string, object?> values, bool autoConnect = true, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(deviceId);
        await EnsureConnectedAsync(entry, autoConnect, cancellationToken);
        foreach (var kv in values)
        {
            var tag = RequireTag(entry, kv.Key);
            if (!tag.Writable) throw new DeviceWriteNotAllowedException($"Tag '{tag.Id}' is read-only.");
        }
        await entry.CommandGate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Driver is IDeviceBatchDriver batch)
            {
                var converted = values.ToDictionary(
                    kv => kv.Key,
                    kv => DeviceValueConversion.ConvertTo(RequireTag(entry, kv.Key).DataType, kv.Value),
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    await batch.WriteManyAsync(converted, cancellationToken);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var kv in converted)
                    {
                        var tag = RequireTag(entry, kv.Key);
                        UpdateSample(entry, new DeviceTagSample(deviceId, tag.Id, tag.DataType, kv.Value, DeviceTagQuality.Good, now));
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref entry.WriteErrors);
                    entry.RuntimeError = ex.Message;
                    throw;
                }
            }

            foreach (var kv in values)
                await WriteTagCoreAsync(entry, kv.Key, kv.Value, cancellationToken);
        }
        finally { entry.CommandGate.Release(); }
    }

    /// <summary>
    /// Executes multiple tag writes as one mutually exclusive device command, in the given order.
    /// Use this for ordered PLC transactions (for example Ready=0, payload, Ready=1): while the command
    /// holds the device lock, all other writes on the same device queue behind it instead of interleaving.
    /// </summary>
    public async Task WriteSequenceAsync(
        string deviceId,
        IReadOnlyList<(string TagId, object? Value)> writes,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var entry = RequireEntry(deviceId);
        await EnsureConnectedAsync(entry, autoConnect, cancellationToken);
        await entry.CommandGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (tagId, value) in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteTagCoreAsync(entry, tagId, value, cancellationToken);
            }
        }
        finally { entry.CommandGate.Release(); }
    }

    public async Task ConnectAsync(string id, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(id);
        await entry.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Driver.ConnectionState == DeviceConnectionState.Connected)
            {
                StartPolling(entry);
                return;
            }

            await entry.Driver.ConnectAsync(cancellationToken);
            entry.RuntimeError = null;
            entry.PollStartedAt = DateTimeOffset.UtcNow;
            StartPolling(entry);
        }
        finally { entry.LifecycleGate.Release(); }
    }

    public async Task DisconnectAsync(string id, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(id);
        await entry.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopPollingAsync(entry);
            await entry.Driver.DisconnectAsync(cancellationToken);
            MarkDisconnected(entry);
        }
        finally { entry.LifecycleGate.Release(); }
    }

    public void ApplySettings(string id, DeviceRuntimeSettings settings)
    {
        var entry = RequireEntry(id);
        entry.Settings = settings.Normalize();
    }

    public async Task<DeviceTagSample> ReadTagAsync(string deviceId, string tagId, bool fresh = false, bool autoConnect = true, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(deviceId);
        await EnsureConnectedAsync(entry, autoConnect, cancellationToken);
        if (!fresh && entry.Values.TryGetValue(tagId, out var cached) && cached.Quality == DeviceTagQuality.Good)
            return cached;

        try
        {
            var sample = await entry.Driver.ReadAsync(tagId, cancellationToken);
            UpdateSample(entry, sample);
            return sample;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref entry.ReadErrors);
            entry.RuntimeError = ex.Message;
            throw;
        }
    }

    public async Task WriteTagAsync(string deviceId, string tagId, object? value, bool autoConnect = true, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(deviceId);
        await EnsureConnectedAsync(entry, autoConnect, cancellationToken);
        await entry.CommandGate.WaitAsync(cancellationToken);
        try
        {
            await WriteTagCoreAsync(entry, tagId, value, cancellationToken);
        }
        finally { entry.CommandGate.Release(); }
    }

    private static async Task WriteTagCoreAsync(Entry entry, string tagId, object? value, CancellationToken cancellationToken)
    {
        var definition = RequireTag(entry, tagId);
        var converted = DeviceValueConversion.ConvertTo(definition.DataType, value);
        try
        {
            await entry.Driver.WriteAsync(tagId, converted, cancellationToken);
            var sample = new DeviceTagSample(entry.Driver.Id, tagId, definition.DataType, converted, DeviceTagQuality.Good, DateTimeOffset.UtcNow);
            UpdateSample(entry, sample);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref entry.WriteErrors);
            entry.RuntimeError = ex.Message;
            throw;
        }
    }

    public async Task<DeviceTagSample> WaitForTagAsync(
        string deviceId,
        string tagId,
        Func<DeviceTagSample, bool> predicate,
        TimeSpan timeout,
        TimeSpan pollInterval,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            try
            {
                var sample = await ReadTagAsync(deviceId, tagId, fresh: true, autoConnect: autoConnect, cancellationToken: timeoutCts.Token);
                if (predicate(sample)) return sample;
                await Task.Delay(pollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for device '{deviceId}' tag '{tagId}' after {timeout.TotalMilliseconds:0} ms.");
            }
        }
    }

    private async Task EnsureConnectedAsync(Entry entry, bool autoConnect, CancellationToken cancellationToken)
    {
        if (entry.Driver.ConnectionState == DeviceConnectionState.Connected) return;
        if (!autoConnect)
            throw new DeviceDisconnectedException($"Device '{entry.Driver.Id}' is {entry.Driver.ConnectionState} and Auto Connect is disabled.");
        await ConnectAsync(entry.Driver.Id, cancellationToken);
    }

    private void StartPolling(Entry entry)
    {
        if (entry.PollTask is { IsCompleted: false }) return;
        entry.PollCts?.Dispose();
        entry.PollCts = new CancellationTokenSource();
        entry.PollTask = Task.Run(() => PollLoopAsync(entry, entry.PollCts.Token));
    }

    private static async Task StopPollingAsync(Entry entry)
    {
        var cts = entry.PollCts;
        var task = entry.PollTask;
        entry.PollCts = null;
        entry.PollTask = null;
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        cts.Dispose();
    }

    private async Task PollLoopAsync(Entry entry, CancellationToken ct)
    {
        var lastHeartbeatWrite = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var settings = entry.Settings.Normalize();
            if (entry.Driver.ConnectionState != DeviceConnectionState.Connected)
            {
                try
                {
                    await entry.Driver.ConnectAsync(ct);
                    Interlocked.Increment(ref entry.ReconnectCount);
                    entry.RuntimeError = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    entry.RuntimeError = ex.Message;
                    MarkDisconnected(entry);
                    await Task.Delay(settings.ReconnectDelayMs, ct);
                    continue;
                }
            }

            var cycleStarted = Stopwatch.GetTimestamp();
            if (entry.Driver is IDeviceBatchDriver batchDriver && entry.Driver.Tags.Count > 0)
            {
                try
                {
                    var samples = await batchDriver.ReadManyAsync(entry.Driver.Tags.Select(x => x.Id).ToArray(), ct);
                    foreach (var sample in samples) UpdateSample(entry, sample);
                    Interlocked.Increment(ref entry.BatchReadCycles);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref entry.ReadErrors);
                    entry.RuntimeError = ex.Message;
                    foreach (var tag in entry.Driver.Tags)
                        entry.Values[tag.Id] = new DeviceTagSample(entry.Driver.Id, tag.Id, tag.DataType, null, DeviceTagQuality.Bad, DateTimeOffset.UtcNow, ex.Message);
                }
            }
            else
            {
                foreach (var tag in entry.Driver.Tags)
                {
                    try
                    {
                        var sample = await entry.Driver.ReadAsync(tag.Id, ct);
                        UpdateSample(entry, sample);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref entry.ReadErrors);
                        entry.RuntimeError = ex.Message;
                        entry.Values[tag.Id] = new DeviceTagSample(entry.Driver.Id, tag.Id, tag.DataType, null, DeviceTagQuality.Bad, DateTimeOffset.UtcNow, ex.Message);
                    }
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastHeartbeatWrite >= TimeSpan.FromMilliseconds(settings.HeartbeatIntervalMs))
            {
                var hostTag = entry.Driver.Tags.FirstOrDefault(x => string.Equals(x.Id, settings.HostHeartbeatTagId, StringComparison.OrdinalIgnoreCase));
                if (hostTag is { Writable: true, DataType: DeviceTagDataType.Boolean })
                {
                    try
                    {
                        entry.HostHeartbeat = !entry.HostHeartbeat;
                        await entry.Driver.WriteAsync(hostTag.Id, entry.HostHeartbeat, ct);
                        UpdateSample(entry, new DeviceTagSample(entry.Driver.Id, hostTag.Id, hostTag.DataType, entry.HostHeartbeat, DeviceTagQuality.Good, now));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref entry.WriteErrors);
                        entry.RuntimeError = ex.Message;
                    }
                }
                lastHeartbeatWrite = now;
            }

            Interlocked.Increment(ref entry.PollCycles);
            entry.LastPollAt = now;
            var elapsedMs = Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
            var delay = Math.Max(1, settings.PollIntervalMs - (int)Math.Ceiling(elapsedMs));
            await Task.Delay(delay, ct);
        }
    }

    private static void UpdateSample(Entry entry, DeviceTagSample sample)
    {
        entry.Values[sample.TagId] = sample;
        if (sample.Quality == DeviceTagQuality.Good)
        {
            entry.LastGoodReadAt = sample.Timestamp;
            if (string.Equals(sample.TagId, entry.Settings.DeviceHeartbeatTagId, StringComparison.OrdinalIgnoreCase))
            {
                var heartbeat = sample.AsBoolean();
                if (heartbeat != entry.DeviceHeartbeat)
                    entry.LastDeviceHeartbeatAt = sample.Timestamp;
                entry.DeviceHeartbeat = heartbeat;
            }
        }
    }

    private static void MarkDisconnected(Entry entry)
    {
        foreach (var tag in entry.Driver.Tags)
        {
            var previous = entry.Values.TryGetValue(tag.Id, out var sample) ? sample.Value : null;
            entry.Values[tag.Id] = new DeviceTagSample(entry.Driver.Id, tag.Id, tag.DataType, previous, DeviceTagQuality.Disconnected, DateTimeOffset.UtcNow, "Device disconnected");
        }
    }

    private static DeviceTagDefinition RequireTag(Entry entry, string tagId)
        => entry.Driver.Tags.FirstOrDefault(x => string.Equals(x.Id, tagId, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceTagNotFoundException($"Device '{entry.Driver.Id}' has no tag '{tagId}'.");

    private Entry RequireEntry(string id)
        => _entries.TryGetValue(id, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Device '{id}' is not registered.");

    private static DeviceDescriptor Snapshot(Entry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(0.001, (now - entry.PollStartedAt).TotalSeconds);
        var pollHz = entry.PollCycles / elapsedSeconds;
        var stats = new DeviceRuntimeStats(
            entry.PollCycles,
            entry.BatchReadCycles,
            entry.ReadErrors,
            entry.WriteErrors,
            entry.ReconnectCount,
            Math.Round(pollHz, 2),
            entry.LastPollAt,
            entry.LastGoodReadAt,
            entry.LastDeviceHeartbeatAt,
            entry.HostHeartbeat,
            entry.DeviceHeartbeat,
            entry.RuntimeError);

        return new DeviceDescriptor(
            entry.Driver.Id,
            entry.Driver.Name,
            entry.Driver.Vendor,
            entry.Driver.Model,
            entry.Driver.Driver,
            entry.Driver.Protocol,
            entry.Driver.Endpoint,
            entry.Driver.ConnectionState,
            entry.Settings,
            stats,
            entry.Driver.Capabilities,
            entry.Driver is IDeviceDiagnosticsProvider diagnostics ? diagnostics.ProtocolDiagnostics : new DeviceProtocolDiagnostics(),
            entry.Driver.Tags,
            DeviceValueConversion.SnapshotDictionary(entry.Values),
            entry.Driver.Error ?? entry.RuntimeError,
            now);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            try { await StopPollingAsync(entry); } catch { }
            try { await entry.Driver.DisposeAsync(); } catch { }
            entry.LifecycleGate.Dispose();
            entry.CommandGate.Dispose();
        }
        _entries.Clear();
    }
}
