using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 计数器快照的文件读写（2026-10-29）。抽成纯函数是为了能在引擎测试里覆盖
/// "写—读—恢复""文件损坏""目录不存在"这些分支，而不用启动整个 Web 宿主。
///
/// 约定：
/// - 写入先落 <c>path + ".tmp"</c> 再原子替换，避免断电留下半截 JSON；
/// - 读取遇到损坏的 JSON 直接抛 <see cref="JsonException"/>，由宿主决定降级策略
///   （当前宿主记 warning 后从零开始）——这里不吞异常，否则"快照文件写坏了"永远查不出来。
/// </summary>
public static class RunCounterSnapshotFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>序列化为文本（持久化去抖时可以直接比较字符串）。</summary>
    public static string Serialize(IReadOnlyList<RunCounterSnapshot> snapshots)
        => JsonSerializer.Serialize(snapshots, Json);

    /// <summary>读取快照；文件不存在返回空列表（首启的正常情况）。</summary>
    public static IReadOnlyList<RunCounterSnapshot> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return [];

        var json = File.ReadAllText(path);
        var snapshots = JsonSerializer.Deserialize<List<RunCounterSnapshot>>(json, Json);
        return snapshots ?? [];
    }

    /// <summary>原子写入快照（临时文件 + 覆盖移动）。</summary>
    public static void Save(string path, IReadOnlyList<RunCounterSnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(snapshots));
        File.Move(temporary, path, overwrite: true);
    }
}
