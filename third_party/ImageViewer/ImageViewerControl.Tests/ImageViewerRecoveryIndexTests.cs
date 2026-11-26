using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 恢复索引的并发契约。
    /// Chinese: 索引是同一台机器上所有窗口/进程共享的一个 JSON 文件。这里的守卫锁住两件事：
    /// ① 并发登记不能丢记录（读-改-写必须原子）；② 写入必须从读者视角是原子的（不能看到半截 JSON）。
    /// English: Guards the recovery index against lost updates under concurrency and against torn writes.
    /// </summary>
    [Trait("Category", "Integration")]
    public class ImageViewerRecoveryIndexTests
    {
        [Fact]
        public void Register_UnderConcurrency_KeepsEveryEntry()
        {
            string rootPath = CreateTempRoot();
            try
            {
                // 每个任务用一个独立的索引实例（模拟多个窗口/进程同时登记），路径也各不相同。
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                const int count = 16;
                string[] paths = Enumerable
                    .Range(0, count)
                    .Select(index => Path.Combine(autoSaveDirectory, $"autosave-document-{index}.ivsession"))
                    .ToArray();
                foreach (string path in paths)
                {
                    File.WriteAllText(path, "session");
                }

                Parallel.For(0, count, position =>
                {
                    var writer = new ImageViewerRecoveryIndex(autoSaveDirectory);
                    writer.Register(paths[position], DateTimeOffset.UtcNow, $"owner-{position}", DateTimeOffset.UtcNow);
                });

                var reader = new ImageViewerRecoveryIndex(autoSaveDirectory);
                var entries = reader.Read();
                Assert.Equal(count, entries.Count);
                Assert.Equal(
                    count,
                    entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public void Register_DoesNotLoseUpdatesWhenWritersAndReadersInterleave()
        {
            string rootPath = CreateTempRoot();
            try
            {
                // 写者与读者交错：原子写（临时文件 + 替换）保证读者永远读到完整 JSON，而不是写了一半的内容。
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                const int count = 24;
                string[] paths = Enumerable
                    .Range(0, count)
                    .Select(index => Path.Combine(autoSaveDirectory, $"autosave-interleaved-{index}.ivsession"))
                    .ToArray();
                foreach (string path in paths)
                {
                    File.WriteAllText(path, "session");
                }

                Parallel.Invoke(paths
                    .Select<string, Action>(path => () => new ImageViewerRecoveryIndex(autoSaveDirectory).Register(path, DateTimeOffset.UtcNow))
                    .Append(() =>
                    {
                        // 与写者并发地反复读取：任何一次读到半截 JSON 都会让索引退化为空（丢记录）。
                        var reader = new ImageViewerRecoveryIndex(autoSaveDirectory);
                        for (int i = 0; i < 200; i++)
                        {
                            _ = reader.Read();
                        }
                    })
                    .ToArray());

                Assert.Equal(count, new ImageViewerRecoveryIndex(autoSaveDirectory).Read().Count);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public void DeleteOwnedSnapshots_RemovesOnlyThatInstancesFilesInsideAutosave()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                string firstPath = Path.Combine(autoSaveDirectory, "autosave-first.ivsession");
                string secondPath = Path.Combine(autoSaveDirectory, "autosave-second.ivsession");
                string outside = Path.Combine(rootPath, "autosave-outside.ivsession");
                File.WriteAllText(firstPath, "session");
                File.WriteAllText(secondPath, "session");
                File.WriteAllText(outside, "user file");

                var index = new ImageViewerRecoveryIndex(autoSaveDirectory);
                index.Register(firstPath, DateTimeOffset.UtcNow, "instance-a", DateTimeOffset.UtcNow);
                index.Register(secondPath, DateTimeOffset.UtcNow, "instance-b", DateTimeOffset.UtcNow);
                index.Register(outside, DateTimeOffset.UtcNow, "instance-a", DateTimeOffset.UtcNow);

                index.DeleteOwnedSnapshots("instance-a");

                Assert.DoesNotContain(index.Read(), entry => entry.Path == firstPath);
                Assert.Contains(index.Read(), entry => entry.Path == secondPath && entry.OwnerInstanceId == "instance-b");
                Assert.False(File.Exists(firstPath));
                Assert.True(File.Exists(secondPath));
                Assert.Equal("user file", File.ReadAllText(outside));
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public void Register_RefreshesHeartbeatAndOwnershipForTheSamePath()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                string autoSavePath = Path.Combine(autoSaveDirectory, "autosave-document.ivsession");
                File.WriteAllText(autoSavePath, "session");

                var index = new ImageViewerRecoveryIndex(autoSaveDirectory);
                DateTimeOffset firstHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-5);
                index.Register(autoSavePath, firstHeartbeat, "instance-a", firstHeartbeat);
                Assert.Equal(firstHeartbeat, Assert.Single(index.Read()).LastHeartbeatUtc);

                DateTimeOffset secondHeartbeat = DateTimeOffset.UtcNow;
                index.Register(autoSavePath, secondHeartbeat, "instance-a", secondHeartbeat);

                ImageViewerRecoveryIndexEntry entry = Assert.Single(index.Read());
                Assert.Equal(secondHeartbeat, entry.TimestampUtc);
                Assert.Equal(secondHeartbeat, entry.LastHeartbeatUtc);
                Assert.Equal("instance-a", entry.OwnerInstanceId);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public void Read_WhenIndexIsCorrupt_DegradesToEmptyInsteadOfThrowing()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                File.WriteAllText(Path.Combine(autoSaveDirectory, "recovery-index.json"), "{ not json");

                var index = new ImageViewerRecoveryIndex(autoSaveDirectory);

                Assert.Empty(index.Read());
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public void Write_LeavesNoTemporaryFileBehind()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                Directory.CreateDirectory(autoSaveDirectory);
                string autoSavePath = Path.Combine(autoSaveDirectory, "autosave-document.ivsession");
                File.WriteAllText(autoSavePath, "session");

                new ImageViewerRecoveryIndex(autoSaveDirectory).Register(autoSavePath, DateTimeOffset.UtcNow);

                string[] remainingFiles = Directory.GetFiles(autoSaveDirectory);
                Assert.DoesNotContain(remainingFiles, file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(remainingFiles, file => string.Equals(Path.GetFileName(file), "recovery-index.json", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        private static string CreateTempRoot()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return rootPath;
        }
    }
}
