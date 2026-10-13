using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Device;

/// <summary>
/// Persists protocol/device mapping separately from Workflow JSON. The workflow contract is the
/// stable Tag ID; protocol addresses can change by replacing a device profile.
/// </summary>
public sealed record DeviceProfile(
    string Id,
    string Kind,
    string Name,
    string Host,
    int Port,
    int TimeoutMs,
    byte UnitId = 1,
    Modbus32BitOrder RegisterOrder = Modbus32BitOrder.ABCD,
    string CpuType = "S71500",
    short Rack = 0,
    short Slot = 0,
    IReadOnlyList<DeviceTagDefinition>? Tags = null,
    DeviceRuntimeSettings? Settings = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>R3-04（2026-10-13 R3 审查）：扫描时发现的问题配置文件——原文件保留，供诊断与人工修复。</summary>
public sealed record DeviceProfileProblem(string FileName, string Error);

/// <summary>
/// 设备配置持久化存储。
///
/// R3-01（2026-10-13 R3 审查）：持久文件名由 ID 清洗而来（非字母/数字/-/_ → '_'），而内存注册表
/// 使用原始 ID——`audit.a` 与 `audit_a` 曾落到同一文件互相覆盖：两次注册都成功、重启后先注册者
/// 的配置静默丢失。修复不是"换一套文件命名"（那会让现场遗留配置失联），而是三件事：
///   1. <see cref="ValidateRegistration"/> 严格 ID 契约——注册入口在任何副作用之前拒绝清洗会
///      改变含义的 ID，持久文件名与 ID 一一对应；遗留（含点号等）配置仍可加载与更新；
///   2. 覆盖（<see cref="SaveAsync"/>）与删除（<see cref="DeleteAsync"/>）前核对**文件内身份**：
///      目标路径若属于另一台设备（遗留碰撞文件）或不可读，一律拒绝，绝不静默覆盖/误删；
///   3. 损坏文件进入 <see cref="Problems"/>（路径 + 原因），由启动日志与健康接口显式报告
///      （R3-04：旧实现静默跳过，设备消失而 /api/health 仍报 ok）。
/// </summary>
public sealed class DeviceProfileStore
{
    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _problemsSync = new();
    private IReadOnlyList<DeviceProfileProblem> _problems = [];

    public DeviceProfileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// R3-04：最近一次 <see cref="ListAsync"/> 扫描发现的问题文件（不可读/损坏/空内容）。
    /// 健康接口与启动日志据此把"配置完整性降级"与"服务可响应"区分开；修复文件后
    /// 下一次扫描（重启或设备列表刷新）即解除。
    /// </summary>
    public IReadOnlyList<DeviceProfileProblem> Problems { get { lock (_problemsSync) return _problems; } }

    public async Task<IReadOnlyList<DeviceProfile>> ListAsync(CancellationToken ct = default)
    {
        var output = new List<DeviceProfile>();
        var problems = new List<DeviceProfileProblem>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var profile = await ReadProfileAsync(path, ct);
                // R3-04：空内容反序列化为 null 同样是"配置不可用"，必须登记而不是与"设备已删除"混同。
                if (profile is null) problems.Add(new DeviceProfileProblem(Path.GetFileName(path), "profile deserialized to null (empty or 'null' content)"));
                else output.Add(profile);
            }
            catch (Exception ex)
            {
                // 损坏配置**保留原文件**并登记（文件名=设备 ID 提示 + 解析原因），不阻止 API 启动。
                problems.Add(new DeviceProfileProblem(Path.GetFileName(path), ex.Message));
            }
        }
        lock (_problemsSync) _problems = problems;
        return output;
    }

    /// <summary>
    /// R3-01/R3-03：注册入口的统一前置校验——必须在任何副作用（内存注册/写盘）之前调用。
    /// 严格 ID 契约：只允许**清洗器原样保留的字符**（字母、数字、连字符、下划线，≤64）——
    /// 满足该契约的 ID 持久文件名与 ID 一一对应，从根上消除清洗碰撞（含中文等 Unicode 字母，
    /// 它们本来就是合法文件名）。违规抛出 <see cref="ArgumentException"/>（API 映射 400）。
    /// 遗留（含点号等）配置仍可加载与更新，但不能再新注册。
    /// </summary>
    public static void ValidateRegistration(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 ||
            profile.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ArgumentException(
                "Device ID may only contain letters, digits, hyphens and underscores (max 64 characters). " +
                "IDs that a filesystem-safe name would rewrite (for example containing '.') are rejected before any state change; " +
                "rename the device with an allowed ID and register it again.", nameof(profile));
        }
        ValidateProfile(profile);
    }

    /// <summary>R3-01：该 ID 对应路径上的持久配置（可能属于另一台设备的遗留碰撞文件）——删除/覆盖前核对归属。</summary>
    public Task<DeviceProfile?> FindAsync(string id, CancellationToken ct = default)
        => ReadProfileAsync(ProfilePath(id), ct);

    public async Task SaveAsync(DeviceProfile profile, CancellationToken ct = default)
    {
        // 结构校验保留（端口范围/kind/host）；ID 严格契约由注册入口执行（遗留配置必须仍可更新）。
        ValidateProfile(profile);
        var normalized = profile with
        {
            Tags = profile.Tags ?? Array.Empty<DeviceTagDefinition>(),
            Settings = (profile.Settings ?? new DeviceRuntimeSettings()).Normalize(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var path = ProfilePath(profile.Id);
        await EnsurePersistedIdentityAsync(path, profile.Id, ct);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, normalized, Json, ct);
        File.Move(temp, path, overwrite: true);
    }

    public async Task UpdateSettingsAsync(string id, DeviceRuntimeSettings settings, CancellationToken ct = default)
    {
        var profile = (await ListAsync(ct)).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        await SaveAsync(profile with { Settings = settings.Normalize() }, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ProfilePath(id);
        if (!File.Exists(path)) return false;
        // R3-01：删除同样核对归属——清洗路径可能命中另一台设备的遗留文件（audit.a ↔ audit_a）。
        await EnsurePersistedIdentityAsync(path, id, ct);
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// R3-01：覆盖/删除前的文件身份核对。目标不存在 ⇒ 放行；存在但不可读或属于另一台设备 ⇒
    /// 拒绝（<see cref="InvalidOperationException"/> → API 409），宁可不写不删，绝不静默破坏。
    /// </summary>
    private async Task EnsurePersistedIdentityAsync(string path, string id, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        DeviceProfile? existing;
        try
        {
            existing = await ReadProfileAsync(path, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Refusing to touch '{Path.GetFileName(path)}' for device '{id}': the existing profile cannot be read ({ex.Message}). " +
                "Repair or remove that file explicitly first.", ex);
        }
        if (existing is null)
            throw new InvalidOperationException(
                $"Refusing to touch '{Path.GetFileName(path)}' for device '{id}': it does not contain a readable device profile.");
        if (!string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Device id '{id}' collides with persisted device '{existing.Id}' (same profile file '{Path.GetFileName(path)}'). " +
                "Rename one of the devices; the existing configuration was preserved.");
    }

    private static async Task<DeviceProfile?> ReadProfileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DeviceProfile>(stream, Json, ct);
    }

    private string ProfilePath(string id)
    {
        var safe = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new InvalidOperationException("Device profile ID is invalid.");
        return Path.Combine(_root, safe + ".json");
    }

    private static void ValidateProfile(DeviceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new InvalidOperationException("Device profile ID is required.");
        if (string.IsNullOrWhiteSpace(profile.Host)) throw new InvalidOperationException("Device host/IP is required.");
        if (profile.Port is < 1 or > 65535) throw new InvalidOperationException("Device port must be between 1 and 65535.");
        if (profile.Kind is not ("modbus-tcp" or "s7")) throw new InvalidOperationException($"Unknown device profile kind '{profile.Kind}'.");
    }
}
