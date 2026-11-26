using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 自动保存恢复索引。
    /// Chinese: 未保存项目在重启后必须还能被找回，而 <c>DocumentId</c> 是每次进程启动新生成的随机值，
    /// 单靠"只认自己 ID 的文件名"会把上一次运行留下的自动保存永远排除在外。索引把已知的自动保存文件
    /// 记录下来，让下次启动可以识别出"这是本机遗留的、待恢复的文档"。
    /// English: Remembers which auto-save files exist on this machine. DocumentId is regenerated per process,
    /// so without an index a restart would never see the previous run's unsaved documents.
    /// </summary>
    /// <remarks>
    /// 并发契约（多进程 + 多线程）：
    /// Chinese: 索引是同一台机器上所有窗口/进程共享的一个文件。多个进程同时自动保存时，
    /// 朴素的"读 → 改 → 写"会互相覆盖（16 个并发登记只剩 1 条）。因此整段读-改-写都在
    /// 一个**跨进程具名互斥量**下执行，且写入用"临时文件 + 原子替换"，让读者永远看不到半截 JSON。
    /// English: The index is one file shared by every window/process on the machine. A plain read-modify-write
    /// loses updates, so the whole transaction runs under a machine-wide named mutex and writes atomically.
    /// </remarks>
    internal sealed class ImageViewerRecoveryIndex
    {
        private const string FileName = "recovery-index.json";
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _filePath;
        private readonly string _directory;
        private readonly string _lockName;

        public ImageViewerRecoveryIndex(string autoSaveDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(autoSaveDirectory);
            _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(autoSaveDirectory));
            _filePath = Path.Combine(_directory, FileName);

            // 互斥量名按目录哈希区分：同一台机器上不同存储根目录的索引互不阻塞，同一目录的所有
            // 进程/线程竞争同一把锁。"Global\" 前缀使其跨会话（服务/终端）可见。
            string directoryHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(autoSaveDirectory).ToUpperInvariant())))
                .ToLowerInvariant()[..16];
            _lockName = $"Global\\ImageViewerRecoveryIndex-{directoryHash}";
        }

        /// <summary>读取索引；文件缺失或损坏时退化为空集合（绝不抛给调用方）。</summary>
        public IReadOnlyList<ImageViewerRecoveryIndexEntry> Read()
        {
            return ExecuteLocked(ReadCore);
        }

        /// <summary>把索引整体写回；失败静默忽略（索引用作提示，不是数据本体）。</summary>
        public void Write(IEnumerable<ImageViewerRecoveryIndexEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            IReadOnlyList<ImageViewerRecoveryIndexEntry> snapshot = [.. entries];
            ExecuteLocked(() =>
            {
                WriteCore(snapshot);
                return true;
            });
        }

        /// <summary>
        /// 登记一个自动保存文件；同路径已存在则更新其时间戳与所有者/心跳。
        /// </summary>
        public void Register(string autoSaveFilePath, ImageViewerRecoveryIndexEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Register(autoSaveFilePath, entry.TimestampUtc, entry.OwnerInstanceId, entry.LastHeartbeatUtc,
                entry.ProjectName, entry.ImageName, entry.HasOwnerLease);
        }

        /// <summary>登记一个自动保存文件；同路径已存在则更新其修改时间。</summary>
        public void Register(string autoSaveFilePath, DateTimeOffset timestamp)
        {
            Register(autoSaveFilePath, timestamp, ownerInstanceId: null, lastHeartbeatUtc: null);
        }

        /// <summary>
        /// 登记（或刷新）一个自动保存文件。
        /// Chinese: 整段读-改-写被互斥量保护，因此并发登记不会丢记录；旧实现只有读和写两次独立 IO，
        /// 两个进程交错时后写的那份直接抹掉先写的。
        /// </summary>
        public void Register(
            string autoSaveFilePath,
            DateTimeOffset timestamp,
            string? ownerInstanceId = null,
            DateTimeOffset? lastHeartbeatUtc = null,
            string? projectName = null,
            string? imageName = null,
            bool hasOwnerLease = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(autoSaveFilePath);
            string fullPath = Path.GetFullPath(autoSaveFilePath);

            ExecuteLocked(() =>
            {
                List<ImageViewerRecoveryIndexEntry> entries = [.. ReadCore()];
                int existing = entries.FindIndex(entry => PathsEqual(entry.Path, fullPath));
                var updated = new ImageViewerRecoveryIndexEntry(fullPath, timestamp)
                {
                    OwnerInstanceId = ownerInstanceId,
                    LastHeartbeatUtc = lastHeartbeatUtc,
                    ProjectName = projectName,
                    ImageName = imageName,
                    HasOwnerLease = hasOwnerLease
                };

                if (existing >= 0)
                {
                    entries[existing] = updated;
                }
                else
                {
                    entries.Add(updated);
                }

                WriteCore(entries);
                return true;
            });
        }

        /// <summary>按路径移除登记项（文件已删除/已被用户永久保存）。</summary>
        public void Unregister(string autoSaveFilePath)
        {
            if (string.IsNullOrWhiteSpace(autoSaveFilePath))
            {
                return;
            }

            string fullPath = Path.GetFullPath(autoSaveFilePath);
            ExecuteLocked(() =>
            {
                List<ImageViewerRecoveryIndexEntry> entries = [.. ReadCore()];
                int removed = entries.RemoveAll(entry => PathsEqual(entry.Path, fullPath));
                if (removed > 0)
                {
                    WriteCore(entries);
                }

                return true;
            });
        }

        /// <summary>移除所有指向已不存在文件的登记项，返回清理后的条目。</summary>
        public IReadOnlyList<ImageViewerRecoveryIndexEntry> Prune()
        {
            return ExecuteLocked(() =>
            {
                List<ImageViewerRecoveryIndexEntry> entries = [.. ReadCore()];
                List<ImageViewerRecoveryIndexEntry> alive = [.. entries.Where(entry => File.Exists(entry.Path))];
                if (alive.Count != entries.Count)
                {
                    WriteCore(alive);
                }

                return (IReadOnlyList<ImageViewerRecoveryIndexEntry>)alive;
            });
        }

        /// <summary>
        /// 释放某个会话实例持有的登记（正常关闭时调用）。
        /// Chinese: 窗口正常关闭后，它留下的自动保存不该再被当成"待恢复文档"提示给下一个窗口。
        /// 异常退出（进程被杀死/崩溃）走不到这里，心跳随之过期，届时才成为可恢复候选。
        /// English: Drops entries owned by a session that shut down cleanly. A crashed process never reaches
        /// this call, so its stale heartbeat is what eventually makes the snapshot recoverable.
        /// </summary>
        public void DeleteOwnedSnapshots(string ownerInstanceId)
        {
            if (string.IsNullOrWhiteSpace(ownerInstanceId))
            {
                return;
            }

            ExecuteLocked(() =>
            {
                List<ImageViewerRecoveryIndexEntry> entries = [.. ReadCore()];
                int removed = entries.RemoveAll(entry =>
                    string.Equals(entry.OwnerInstanceId, ownerInstanceId, StringComparison.Ordinal) &&
                    TryDeleteOwnedFile(entry.Path));
                if (removed > 0)
                {
                    WriteCore(entries);
                }

                return true;
            });
        }

        public FileStream? TryAcquireOwnerLease(string ownerInstanceId)
        {
            try
            {
                return new FileStream(GetOwnerLeasePath(ownerInstanceId), FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null; // A storage provider that cannot hold the lease still uses the heartbeat.
            }
        }

        public bool IsOwnerActive(string? ownerInstanceId)
        {
            if (!Guid.TryParseExact(ownerInstanceId, "N", out _))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(GetOwnerLeasePath(ownerInstanceId!), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private string GetOwnerLeasePath(string ownerInstanceId) => Path.Combine(_directory, $".owner-{ownerInstanceId}");

        private bool TryDeleteOwnedFile(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string name = Path.GetFileName(fullPath);
                if (!PathsEqual(Path.GetDirectoryName(fullPath) ?? string.Empty, _directory) ||
                    !name.StartsWith("autosave-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".ivsession", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                File.Delete(fullPath);
                ImageViewerSnapshotImage.PruneRecoveryImages(fullPath);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private IReadOnlyList<ImageViewerRecoveryIndexEntry> ReadCore()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return [];
                }

                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return [];
                }

                ImageViewerRecoveryIndexDocument? document = JsonSerializer.Deserialize<ImageViewerRecoveryIndexDocument>(json, SerializerOptions);
                return document?.Entries ?? [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// 原子写入：先写同目录下的临时文件，再整体替换目标。
        /// Chinese: 直接 <c>File.WriteAllText</c> 会让并发读者看到"写了一半"的 JSON（反序列化失败 → 索引退化为空
        /// → 恢复候选全部消失）。<c>File.Move(overwrite: true)</c> 在 NTFS 上是替换语义，读者要么看到旧内容、
        /// 要么看到新内容。
        /// English: Writes to a temp file then atomically replaces the target, so a concurrent reader can never
        /// observe a half-written JSON document.
        /// </summary>
        private void WriteCore(IReadOnlyList<ImageViewerRecoveryIndexEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var document = new ImageViewerRecoveryIndexDocument { Entries = [.. entries] };
                string json = JsonSerializer.Serialize(document, SerializerOptions);

                string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 索引写不进去只影响"重启后能否提示恢复"，不能因此打断保存流程。
            }
        }

        /// <summary>
        /// 在跨进程互斥量下执行一段索引事务。
        /// Chinese: 拿不到锁（超时/权限）时**不抛异常**——索引是提示性设施，退化为"本次不改索引"即可，
        /// 绝不能把锁竞争变成保存失败。返回默认值让调用方继续走主流程。
        /// </summary>
        private T ExecuteLocked<T>(Func<T> action)
        {
            try
            {
                using Mutex mutex = new(initiallyOwned: false, _lockName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(LockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者崩溃：锁已归我们，索引可能处于半写状态，但原子写保证文件本身完好。
                    acquired = true;
                }

                if (!acquired)
                {
                    return default!;
                }

                try
                {
                    return action();
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException or WaitHandleCannotBeOpenedException)
            {
                return default!;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    internal sealed class ImageViewerRecoveryIndexDocument
    {
        public List<ImageViewerRecoveryIndexEntry> Entries { get; set; } = [];
    }

    /// <summary>索引里的单条自动保存记录。</summary>
    internal sealed class ImageViewerRecoveryIndexEntry
    {
        public ImageViewerRecoveryIndexEntry()
        {
        }

        public ImageViewerRecoveryIndexEntry(string path, DateTimeOffset timestamp)
        {
            Path = path;
            TimestampUtc = timestamp;
        }

        /// <summary>自动保存文件的绝对路径。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>最后一次成功写入的时间（UTC）。</summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// 写这份自动保存的会话实例标识。
        /// Chinese: 每个 viewer 实例一个随机 id（与文件名用的 DocumentId 不同，后者会随项目路径改变）。
        /// 只有"仍在运行且心跳新鲜"的实例，其快照才不该被别的窗口当作可恢复文档。
        /// English: Identity of the session instance that wrote this snapshot; used to tell "still open"
        /// from "ended/crashed".
        /// </summary>
        public string? OwnerInstanceId { get; set; }

        /// <summary>
        /// 所有者最后一次心跳（UTC）。
        /// Chinese: 每次成功自动保存都会刷新，因此"心跳比自动保存间隔还旧很多"就意味着该实例已消失
        /// （崩溃/被强杀，走不到正常关闭的清理路径），这份快照即可恢复。
        /// </summary>
        public DateTimeOffset? LastHeartbeatUtc { get; set; }

        public string? ProjectName { get; set; }

        public string? ImageName { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HasOwnerLease { get; set; }
    }
}
