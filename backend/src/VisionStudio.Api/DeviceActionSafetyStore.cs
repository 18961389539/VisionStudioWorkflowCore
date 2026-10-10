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
public sealed class DeviceActionSafetyStore
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

    public string StatePath => _path;

    public DeviceActionSafetyState? Load()
    {
        try
        {
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
    public void Save(DeviceActionSafetyState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _path, true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            // 删除失败会让下次启动仍然要求核对（保守方向）；记录以便运维手动清理。
            _logger.LogWarning(ex, "Device-action safety state at {Path} could not be deleted; the verification gate stays engaged after a restart.", _path);
        }
    }
}

/// <summary>
/// F01/F02 持久化载荷：未知动作标记 + 故障时锁定的设备/机器人 ID 清单 + 原因与时间。
/// 核对必须针对该清单（而不是新发布流程的清单）——切换流程不能成为绕过核对的路径。
/// </summary>
public sealed record DeviceActionSafetyState(
    string ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    DateTimeOffset SetAt,
    string Reason,
    bool Unreadable = false);
