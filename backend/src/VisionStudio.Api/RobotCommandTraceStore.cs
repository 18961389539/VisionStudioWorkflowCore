using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api;

public sealed record RobotCommandTraceRecord(
    string TraceId,
    string RobotId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long CommandId,
    int MaxAttempt,
    string LastStage,
    string Status,
    VisionRobotTarget2D? Target,
    string? Error,
    IReadOnlyList<RobotCommandTraceEvent> Events);

/// <summary>
/// File-backed command trace observer. Each handshake trace is stored independently from vision RunTrace so
/// robot communication failures can be inspected even when no workflow was running.
/// </summary>
public sealed class RobotCommandTraceStore : IRobotCommandObserver
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RobotCommandTraceStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "data", "robot-traces");
        Directory.CreateDirectory(_root);
    }

    public async ValueTask OnEventAsync(RobotCommandTraceEvent traceEvent, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = FindPath(traceEvent.TraceId) ?? TracePath(traceEvent.Timestamp, traceEvent.TraceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var current = File.Exists(path) ? await ReadAsync<RobotCommandTraceRecord>(path, cancellationToken) : null;
            var events = current?.Events.ToList() ?? [];
            events.Add(traceEvent);
            var status = traceEvent.Stage switch
            {
                "Done" => "Complete",
                "Error" => "Error",
                "Retry" => "Retrying",
                _ => "Running"
            };
            var record = new RobotCommandTraceRecord(
                traceEvent.TraceId,
                traceEvent.RobotId,
                current?.StartedAt ?? traceEvent.Timestamp,
                traceEvent.Timestamp,
                traceEvent.CommandId != 0 ? traceEvent.CommandId : current?.CommandId ?? 0,
                Math.Max(traceEvent.Attempt, current?.MaxAttempt ?? 0),
                traceEvent.Stage,
                status,
                traceEvent.Target ?? current?.Target,
                traceEvent.Error ?? current?.Error,
                events);
            await WriteAsync(path, record, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RobotCommandTraceRecord>> ListAsync(int take, string? robotId, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return [];
        var result = new List<RobotCommandTraceRecord>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var item = await ReadAsync<RobotCommandTraceRecord>(path, ct);
                if (item is null) continue;
                if (!string.IsNullOrWhiteSpace(robotId) && !string.Equals(item.RobotId, robotId, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(item);
            }
            catch { }
        }
        return result.OrderByDescending(x => x.StartedAt).Take(Math.Clamp(take, 1, 500)).ToArray();
    }

    public async Task<RobotCommandTraceRecord?> GetAsync(string traceId, CancellationToken ct)
    {
        var path = FindPath(traceId);
        return path is null ? null : await ReadAsync<RobotCommandTraceRecord>(path, ct);
    }

    private string TracePath(DateTimeOffset at, string traceId)
        => Path.Combine(_root, at.UtcDateTime.ToString("yyyy-MM-dd"), traceId + ".json");

    private string? FindPath(string traceId)
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, traceId + ".json", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, _json, ct);
        File.Move(temp, path, true);
    }
}
