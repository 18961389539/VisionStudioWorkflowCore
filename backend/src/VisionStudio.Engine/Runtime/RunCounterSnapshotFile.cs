using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 计数器快照的文件读写（2026-10-29）。抽成纯函数是为了能在引擎测试里覆盖
/// "写—读—恢复""文件损坏""目录不存在"这些分支，而不用启动整个 Web 宿主。
///
/// 约定：
/// - 写入先落 <c>path + ".tmp"</c>，**强制刷到物理介质**后再原子替换。只做临时文件 + rename
///   是不够的：<see cref="File.WriteAllText"/> 只把内容交给 OS 页缓存，rename 只保证目录项
///   原子，两种情况都不保证数据已落盘。工控现场断电是常态，缺这一步会出现"文件存在但内容
///   是上一次快照"——计数器静默回退，现场只会以为视觉算错了；
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

    /// <summary>
    /// 原子且**持久**地写入快照（临时文件 → 刷盘 → 覆盖移动），并在写之前取跨进程写锁。
    ///
    /// 顺序不能交换：先 <see cref="FileStream.Flush(bool)"/> 把内容压到物理介质，再 rename。
    /// 反过来的话断电可能留下"新文件名 + 旧/空内容"——这是最难查的一类数据损坏，
    /// 因为文件存在、能解析、数值却退回了上一班次。
    ///
    /// **临时文件名带进程 id 与随机后缀**，不是固定的 <c>path + ".tmp"</c>。固定名在两个宿主
    /// 重叠运行（升级重启：旧进程还在 StopAsync 补写，新进程已经启动）时会互相截断：
    /// A 刷完盘关闭 → B 以 Create 打开同名临时文件（截断）→ A rename，把 B 的**半成品**搬到正式位置。
    /// 下次启动读到半个 JSON 就走"损坏 → 从零开始"，整班次良率清零。唯一名字让这种交叉不可能发生。
    /// </summary>
    public static void Save(string path, IReadOnlyList<RunCounterSnapshot> snapshots)
        => Save(path, snapshots, WriteLockTimeout);

    /// <summary>带自定义等锁预算的写入（测试用短预算验证"抢不到锁时显式失败"）。</summary>
    public static void Save(string path, IReadOnlyList<RunCounterSnapshot> snapshots, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 跨进程互斥：两个宿主共用一个数据根时，宁可让后写的一方失败并上报"降级"，
        // 也不要让两份快照交替覆盖——现场只会看到计数无缘无故回退。
        using var writeLock = AcquireWriteLock(path, lockTimeout);

        var temporary = $"{path}.{Environment.ProcessId:x}.{Guid.NewGuid():n}.tmp";
        var bytes = Utf8.GetBytes(Serialize(snapshots));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                // 真正的 fsync：绕过 OS 页缓存，确保断电后 rename 指向的内容是完整的。
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // 失败时清掉半成品：磁盘满的场景下每个采样窗口都会留一个残片，不清会慢慢吃满剩余空间。
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // 原始异常（写失败/锁冲突）更有诊断价值，清理失败不能把它盖掉。
            }

            throw;
        }
    }

    /// <summary>
    /// 清掉进程被强杀时遗留的半成品临时文件（只在启动阶段调用）。
    ///
    /// 正常路径不会留下临时文件（成功是 rename 走，失败在 catch 里删），
    /// 只有"写到一半断电/被 Kill"会残留，一次一个。现场每天都断电，不清就一年三百个。
    /// </summary>
    /// <returns>清理掉的文件个数。</returns>
    public static int DeleteAbandonedTemporaries(string path, TimeSpan olderThan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

        // 年龄门槛不能省：另一个宿主可能**正在**写自己的临时文件（名字不同，不会被误删内容，
        // 但会在 rename 时找不到文件而失败）。只清明显过期的，给重叠运行的窗口留出余地。
        var cutoff = DateTime.UtcNow - olderThan;
        var name = Path.GetFileName(path);
        var removed = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, name + ".*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(candidate) > cutoff) continue;
                File.Delete(candidate);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 别的进程还在写就留着，下次启动再说。
            }
        }

        // 旧版本用的是固定名 path + ".tmp"，升级后可能残留一个。
        var legacy = path + ".tmp";
        try
        {
            if (File.Exists(legacy) && File.GetLastWriteTimeUtc(legacy) <= cutoff)
            {
                File.Delete(legacy);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }

    /// <summary>默认等锁预算：写盘是后台任务，等 5s 比"直接放弃一个采样窗口"更划算。</summary>
    public static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>抢锁轮询间隔。</summary>
    private const int WriteLockPollMs = 25;

    /// <summary>
    /// 以 <see cref="FileShare.None"/> 独占打开 <c>path + ".lock"</c>，拿不到就轮询到超时。
    ///
    /// 用独立锁文件而不是直接锁快照：快照是 rename 替换的，锁住旧文件挡不住任何人。
    /// 超时抛 <see cref="IOException"/>，由宿主的失败重试路径兜住并上报降级——
    /// "两个宿主共用一个数据根"必须变成可见事件，而不是静默的计数回退。
    ///
    /// 两个平台细节（2026-10-29 实测）：
    /// - 打开**已存在的、被别人以 FileShare.None 占住**的文件，Windows 抛的是
    ///   <see cref="UnauthorizedAccessException"/>（ERROR_ACCESS_DENIED），不是 IOException。
    ///   只按 IOException 重试的话，真正的跨进程争用会立刻失败、5s 预算形同虚设。
    /// - 但 UnauthorizedAccessException 也覆盖"路径被同名目录占住/ACL 拒绝"这类**永久**错误，
    ///   对它们重试只是白等一个采样窗口。用 <see cref="Directory.Exists"/> 把两者分开：
    ///   路径是目录 → 立刻失败；否则按争用处理，等到预算用完。
    /// </summary>
    private static FileStream AcquireWriteLock(string path, TimeSpan timeout)
    {
        var lockPath = path + ".lock";
        var budget = (long)Math.Max(0, timeout.TotalMilliseconds);
        var deadline = Environment.TickCount64 + budget;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(lockPath) || Environment.TickCount64 >= deadline)
                {
                    throw new IOException(
                        $"Could not acquire the run counter snapshot lock '{lockPath}' within {budget:0} ms. " +
                        "Another VisionStudio host is probably running against the same data root; " +
                        "counters from only one of them will reach disk.", ex);
                }

                Thread.Sleep(WriteLockPollMs);
            }
        }
    }

    /// <summary>UTF-8 无 BOM：与 <see cref="File.WriteAllText(string, string)"/> 的默认编码一致。</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
}
