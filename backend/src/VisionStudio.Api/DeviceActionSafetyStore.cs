using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>
/// F01/F02：生产"设备动作不确定"安全状态的持久化存储。
///
/// 副作用流程失败（含取消导致的失败）后，未知动作标记与**故障时锁定的**设备/机器人清单写入
/// 数据根（data/production，随系统备份往返），重启后加载——此前该状态仅存内存，重启即丢失，
/// 自动启动/人工启动都会跳过设备核对。
///
/// 路径可用配置 <c>Production:DeviceActionSafetyFile</c> 覆盖（测试宿主据此把状态隔离到
/// 自己的临时目录，避免共享 contentRoot 的宿主通过该文件互相阻断）。
///
/// 语义约束：
/// - 文件存在 ⇒ 必须经过核对（Start/Recover 闸门）或人工处理才能清除；Stop 不清除、重启保留；
/// - 文件损坏 ⇒ 视为"无法核对"（保守拒绝启动），绝不能当作"无未知动作"；
/// - 核对必须针对持久化清单本身（见 ProductionRuntime.EnsureNoUnknownDeviceActionsAsync）。
/// </summary>
public class DeviceActionSafetyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly ILogger<DeviceActionSafetyStore> _logger;

    public DeviceActionSafetyStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<DeviceActionSafetyStore> logger)
    {
        var configured = configuration.GetValue<string>("Production:DeviceActionSafetyFile");
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production", "device-action-safety.json")
            : configured;
        _logger = logger;
    }

    public DeviceActionSafetyStore(string path, ILogger<DeviceActionSafetyStore> logger)
    {
        _path = Path.GetFullPath(path);
        _logger = logger;
    }

    public string StatePath => _path;
    public string ResolutionDirectory => _path + ".resolutions";

    /// <summary>
    /// R01：当前进程标识。用来区分"本进程仍在进行中的意图"与"上一个进程崩溃遗留的意图"——
    /// 后者必须阻断所有新的设备动作入口（重启后无核对不得重发）。
    /// </summary>
    public static readonly string CurrentProcessId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// R01：判定持久化状态是否仍"未闭合"。
    /// · Unknown（副作用失败后保留）→ 一律未闭合；
    /// · IntentRecorded 且 ProcessId 与当前进程不同（含旧格式缺字段）→ 崩溃遗留 → 未闭合；
    /// · IntentRecorded 且属于当前进程 → 本进程活跃意图，不是跨进程残留（并发由租约仲裁）。
    /// </summary>
    public static bool IsUnresolved(DeviceActionSafetyState state)
        => state.Phase != DeviceActionSafetyPhase.IntentRecorded ||
           !string.Equals(state.ProcessId, CurrentProcessId, StringComparison.Ordinal);

    /// <summary>R01：仅当持久化状态仍属于指定 RunId 时清除——绝不误删另一个组件登记的意图。</summary>
    public bool ClearIfOwned(string runId)
    {
        var current = Load();
        if (current is null) return true;
        if (!string.Equals(current.RunId, runId, StringComparison.Ordinal)) return false;
        Clear();
        return true;
    }

    public virtual DeviceActionSafetyState? Load()
    {
        try
        {
            if (Directory.Exists(_path)) throw new IOException("Safety-state path is a directory, not a state file.");
            if (!File.Exists(_path)) return null;
            var state = JsonSerializer.Deserialize<DeviceActionSafetyState>(File.ReadAllText(_path), Json);
            if (state is null)
            {
                _logger.LogError("Device-action safety state at {Path} deserialized to null; treating it as unreadable.", _path);
                return Unreadable();
            }
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device-action safety state at {Path} could not be read; treating it as unreadable (production start requires manual resolution).", _path);
            return Unreadable();
        }

        static DeviceActionSafetyState Unreadable()
            => new("unreadable", [], [], DateTimeOffset.UtcNow, "safety state file could not be read", Unreadable: true);
    }

    /// <summary>原子写入（temp + move）：绝不让半写文件被下一次启动读到。</summary>
    public virtual void Save(DeviceActionSafetyState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, true);
    }

    public virtual void Clear()
    {
        if (Directory.Exists(_path)) throw new IOException("Safety-state path is a directory, not a state file.");
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Durably retain who reconciled an unknown action and the evidence used before clearing its gate.</summary>
    public virtual void SaveResolution(DeviceActionSafetyResolution resolution)
    {
        var directory = ResolutionDirectory;
        Directory.CreateDirectory(directory);
        var safeRunId = new string(resolution.RunId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeRunId)) safeRunId = "legacy";
        var path = Path.Combine(directory, $"{safeRunId}-{resolution.ResolvedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, resolution, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, true);
    }
}

/// <summary>
/// F01/F02 持久化载荷：未知动作标记 + 故障时锁定的设备/机器人 ID 清单 + 原因与时间。
/// 核对必须针对该清单（而不是新发布流程的清单）——切换流程不能成为绕过核对的路径。
/// Q01：Phase 区分"副作用周期进行中已完成意图登记"（IntentRecorded，进程被杀也阻断）与
/// "动作结果未知"（Unknown）。旧格式文件缺省反序列化为 Unknown（保守方向）。
/// Q02：DeviceFingerprints 记录执行动作时的设备身份（driver|protocol|endpoint），
/// 核对时检测"换成另一台设备"的漂移。
/// </summary>
public sealed record DeviceActionSafetyState(
    string ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    DateTimeOffset SetAt,
    string Reason,
    bool Unreadable = false,
    DeviceActionSafetyPhase Phase = DeviceActionSafetyPhase.Unknown,
    string? RunId = null,
    IReadOnlyDictionary<string, string>? DeviceFingerprints = null,
    string? ProcessId = null);

public enum DeviceActionSafetyPhase
{
    /// <summary>默认（含旧格式文件）：动作结果未知，必须核对。</summary>
    Unknown = 0,
    /// <summary>Q01：副作用周期开始前登记的耐久意图——周期未正常收尾前一直有效。</summary>
    IntentRecorded = 1
}

public sealed record DeviceActionSafetyResolution(
    string RunId,
    string ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    string Operator,
    string Reason,
    string Evidence,
    string DeviceIdentityEvidence,
    DateTimeOffset ResolvedAt);
