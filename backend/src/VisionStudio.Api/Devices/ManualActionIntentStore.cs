using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>
/// F03/Fix3：一次管理员核对的**耐久证据**——被核对意图的完整快照 + 操作者 + 现场与身份证据全文。
/// 只在证据落盘成功后才允许解除意图（顺序不可颠倒，否则"先解除、后写审计"会留下无证据的放行）。
/// </summary>
public sealed record ManualActionResolution(
    string RunId,
    string Action,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyList<string> CameraIds,
    string? OriginalProcessId,
    ManualActionIntentState OriginalState,
    string? OriginalClosedReason,
    string OperatorName,
    string Reason,
    string Evidence,
    string DeviceIdentityEvidence,
    DateTimeOffset ResolvedAt,
    /// <summary>N07：被核对意图关联的真实执行标识（运行 RunId / 调试会话 ID）——核对记录必须能定位原执行。</summary>
    string? RelatedRunId = null);

/// <summary>R01/F02：一条手动/临时运行的设备动作意图（耐久登记，进程崩溃后仍可追溯）。</summary>
/// <param name="State">
/// F02：意图生命周期。Issued = 动作已下发、结果未确认（同进程视为活跃，其它进程视为遗留）；
/// Unknown = 结果未确认或失败（**任何进程**都必须先核对才能继续动作）。
/// </param>
public sealed record ManualActionIntent(
    string RunId,
    string Action,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyList<string> CameraIds,
    DateTimeOffset SetAt,
    string? ProcessId,
    ManualActionIntentState State = ManualActionIntentState.Issued,
    string? ClosedReason = null,
    DateTimeOffset? ClosedAt = null,
    /// <summary>C07：关联的真实执行标识（运行 RunId / 调试会话 ID）——核对记录必须能定位原执行。</summary>
    string? RelatedRunId = null);

public enum ManualActionIntentState
{
    /// <summary>动作已下发但结果尚未确认；同进程内的活跃意图不阻断其它入口（并发由租约仲裁）。</summary>
    Issued = 0,
    /// <summary>F02：结果未确认（失败/取消/异步未完成/进程中断）——必须核对，任何进程都阻断。</summary>
    Unknown = 1
}

/// <summary>
/// R01/F02：手动/临时运行动作意图的持久化存储（独立文件、原子写、显式格式版本）。
///
/// 与生产安全状态（device-action-safety.json，单条语义）分开保存，避免手动动作与生产周期
/// 互相覆盖对方的意图；两者都由 <see cref="DeviceActionAuthorizationService"/> 统一读取。
///
/// 语义约束（F02 加固）：
/// · 空文件 / JSON null / 结构缺字段 ⇒ 抛异常（调用方按"未闭合"处理，保守阻断），
///   绝不当作"没有意图"（此前 null 被解析为"无意图"，是探针确认的绕过路径）；
/// · 写入使用 FileStream + Flush(true)（WriteThrough），并为文件加格式版本；
/// · 读取兼容历史裸数组格式。
/// </summary>
public class ManualActionIntentStore
{
    private const int FormatVersion = 1;
    // 审计/台账文件必须**人类可读**：默认编码器会把中文证据转义成 \uXXXX，现场导出后无法直接阅读。
    // UnsafeRelaxedJsonEscaping 只用于落盘 JSON（非 HTML 上下文），保留 UTF-8 明文。
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string _path;
    private readonly ILogger<ManualActionIntentStore> _logger;
    private readonly object _sync = new();
    private List<ManualActionResolution>? _resolutionsCache;
    private DateTime _resolutionsDirectoryWriteUtc;

    public ManualActionIntentStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<ManualActionIntentStore> logger)
    {
        var configured = configuration.GetValue<string>("Production:ManualActionIntentFile");
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath, configuration), "production", "manual-action-intents.json")
            : configured;
        _logger = logger;
    }

    public string StatePath => _path;

    /// <summary>F03：核对证据目录（与被核对的意图台账同根，便于现场整体导出）。</summary>
    public string ResolutionDirectory => _path + ".resolutions";

    /// <summary>F03/Fix3：耐久保存核对证据（含被核对意图快照）——必须先于解除意图完成。</summary>
    public virtual void SaveResolution(ManualActionResolution resolution)
    {
        lock (_sync)
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
            if (_resolutionsCache is not null)
            {
                _resolutionsCache.Add(resolution);
                _resolutionsDirectoryWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            }
        }
    }

    /// <summary>
    /// A04：读取全部**耐久核对证据**（ResolveDirectory 下的记录）。证据是核对闭合的耐久事实——
    /// 启动时据此一致识别已核对意图（台账行删除失败/进程被杀都不会让同一记录重新要求核对）。
    /// 无法解析的证据文件只告警跳过（保守方向：对应意图继续要求核对）。
    /// </summary>
    public virtual IReadOnlyList<ManualActionResolution> LoadResolutions()
    {
        lock (_sync)
        {
            var directory = ResolutionDirectory;
            if (!Directory.Exists(directory))
            {
                _resolutionsCache = null;
                return [];
            }

            var currentWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            if (_resolutionsCache is not null && currentWriteUtc == _resolutionsDirectoryWriteUtc)
                return _resolutionsCache.ToArray();

            var list = new List<ManualActionResolution>();
            foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var resolution = JsonSerializer.Deserialize<ManualActionResolution>(File.ReadAllText(file), Json);
                    if (resolution is not null) list.Add(resolution);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Manual action resolution evidence file {Path} could not be read; its intent stays unresolved.", file);
                }
            }
            _resolutionsCache = list;
            _resolutionsDirectoryWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            return list.ToArray();
        }
    }

    /// <summary>A04：指定意图的最新耐久核对证据（重复核对的幂等依据）。</summary>
    public virtual ManualActionResolution? TryGetResolution(string runId)
        => LoadResolutions().LastOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));

    public virtual IReadOnlyList<ManualActionIntent> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                var text = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("the intent file is empty");
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Null)
                    throw new InvalidOperationException("the intent file contains JSON null");
                var list = root.ValueKind switch
                {
                    JsonValueKind.Array => JsonSerializer.Deserialize<List<ManualActionIntent>>(text, Json) ?? throw new InvalidOperationException("array payload deserialized to null"),
                    JsonValueKind.Object when root.TryGetProperty("intents", out _) => DeserializeWrapped(root) ?? throw new InvalidOperationException("wrapped payload has no intents array"),
                    _ => throw new InvalidOperationException($"unexpected JSON payload kind '{root.ValueKind}'")
                };
                foreach (var intent in list)
                {
                    if (string.IsNullOrWhiteSpace(intent.RunId) || string.IsNullOrWhiteSpace(intent.Action))
                        throw new InvalidOperationException("an intent entry is missing required fields (runId/action)");
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual action intent file at {Path} could not be read; device actions stay blocked until it is resolved.", _path);
                throw new InvalidOperationException($"The manual action intent file could not be read ({ex.Message}).", ex);
            }
        }
    }

    private static List<ManualActionIntent>? DeserializeWrapped(JsonElement root)
    {
        if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var value) || value < 1)
            throw new InvalidOperationException("the intent file has no valid formatVersion");
        return root.GetProperty("intents").Deserialize<List<ManualActionIntent>>(Json);
    }

    public virtual void Add(ManualActionIntent intent)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            list.Add(intent);
            Write(list);
        }
    }

    public virtual void Remove(string runId)
    {
        lock (_sync)
        {
            var list = Load().Where(x => !string.Equals(x.RunId, runId, StringComparison.Ordinal)).ToList();
            Write(list);
        }
    }

    /// <summary>F02：把意图标记为"结果未确认"——比删除更保守，重启/同进程都会继续阻断。</summary>
    public virtual void MarkUnknown(string runId, string reason)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            var index = list.FindIndex(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));
            if (index < 0) return;
            list[index] = list[index] with
            {
                State = ManualActionIntentState.Unknown,
                ClosedReason = reason,
                ClosedAt = DateTimeOffset.UtcNow
            };
            Write(list);
        }
    }

    /// <summary>F02：耐久写入（WriteThrough）——动作前记录必须在断电/崩溃前确定落盘。</summary>
    private void Write(List<ManualActionIntent> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = FormatVersion, intents = list }, Json);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}
