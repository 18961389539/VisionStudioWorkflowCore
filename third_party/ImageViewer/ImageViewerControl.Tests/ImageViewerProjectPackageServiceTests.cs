using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Integration")]
    public class ImageViewerProjectPackageServiceTests
    {
        [Fact]
        public async Task ExportAsync_WithoutImageFile_WritesSessionOnly()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "sample.ivproject");
                var service = CreateService(rootPath);

                await service.ExportAsync(
                    packagePath,
                    CreateSnapshot(Path.Combine(rootPath, "missing.png"), [new CircleRoi()]),
                    RoiPluginRegistry.CreateBuiltIn());

                using ZipArchive archive = ZipFile.OpenRead(packagePath);
                Assert.NotNull(archive.GetEntry("session.ivsession"));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task ExportAsync_WithExistingImage_PacksImageUnderAssets()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string imagePath = Path.Combine(rootPath, "source.png");
                await File.WriteAllTextAsync(imagePath, "image");
                string packagePath = Path.Combine(rootPath, "sample.ivproject");
                var service = CreateService(rootPath);

                await service.ExportAsync(
                    packagePath,
                    CreateSnapshot(imagePath, []),
                    RoiPluginRegistry.CreateBuiltIn());

                using ZipArchive archive = ZipFile.OpenRead(packagePath);
                ZipArchiveEntry imageEntry = Assert.Single(archive.Entries, entry => entry.FullName == "assets/source.png");
                Assert.Equal("image", await ReadEntryAsync(imageEntry));
                ImageViewerSessionData session = await service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(imagePath))), session.ImageSha256);
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task ExportAsync_ThenLoadAsync_RoundTripsSnapshotThroughPackage()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "round-trip.ivproject");
                var service = CreateService(rootPath);
                var registry = RoiPluginRegistry.CreateBuiltIn();
                var snapshot = CreateSnapshot(
                    null,
                    [new CircleRoi { Label = "packaged", Center = new PointD(12, 34), Radius = 5 }],
                    pixelSize: 0.5,
                    physicalUnit: "mm") with
                {
                    Scale = 1.25,
                    TranslateX = 24,
                    TranslateY = -12
                };

                await service.ExportAsync(packagePath, snapshot, registry);
                ImageViewerSessionData result = await service.LoadAsync(packagePath, registry);

                Assert.Equal(0.5, result.PixelSize);
                Assert.Equal("mm", result.PhysicalUnit);
                Assert.Equal(1.25, result.Scale);
                Assert.Equal(24, result.TranslateX);
                Assert.Equal(-12, result.TranslateY);
                var circle = Assert.IsType<CircleRoi>(Assert.Single(result.Rois));
                Assert.Equal("packaged", circle.Label);
                Assert.Equal(new PointD(12, 34), circle.Center);
                Assert.Equal(5, circle.Radius);
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        /// <summary>
        /// 改造前导出的项目包必须继续可读。
        /// Chinese: 包内 session.ivsession 用的是历史转义字符串格式且没有会话版本号。
        /// English: The packaged session uses the legacy escaped-string payload and has no session version.
        /// </summary>
        [Fact]
        public async Task LoadAsync_LegacyEscapedSessionEntry_RemainsReadable()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Session", "legacy-v1-escaped-session.json");
                string packagePath = Path.Combine(rootPath, "legacy.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    // 旧会话引用了 assets/source.png：新校验要求引用的资产真实存在于包内（应用生成的包总是如此）。
                    ZipArchiveEntry assetEntry = archive.CreateEntry("assets/source.png");
                    using (Stream assetStream = assetEntry.Open())
                    {
                        assetStream.Write([1, 2, 3, 4]);
                    }

                    ZipArchiveEntry sessionEntry = archive.CreateEntry("session.ivsession");
                    using Stream stream = sessionEntry.Open();
                    using var writer = new StreamWriter(stream);
                    writer.Write(File.ReadAllText(fixturePath));
                }

                var service = CreateService(rootPath);
                ImageViewerSessionData result = await service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn());

                Assert.Equal("legacy-session", result.SessionName);
                Assert.Equal(0.5, result.PixelSize);
                Assert.Equal("mm", result.PhysicalUnit);
                Assert.Equal(1.25, result.Scale);
                var circle = Assert.IsType<CircleRoi>(Assert.Single(result.Rois));
                Assert.Equal("legacy-circle", circle.Label);
                Assert.Equal(15, circle.Radius);
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_MissingSessionEntry_ThrowsInvalidDataException()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "missing-session.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    archive.CreateEntry("assets/source.png");
                }

                var service = CreateService(rootPath);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn()));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_RejectsZipSlipEntryPath()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "unsafe.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    archive.CreateEntry("session.ivsession");
                    archive.CreateEntry("assets/../../outside.txt");
                }

                var service = CreateService(rootPath);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn()));
                Assert.False(File.Exists(Path.Combine(rootPath, "outside.txt")));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_RejectsPackageWithTooManyEntries()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "too-many-entries.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    archive.CreateEntry("session.ivsession");
                    for (int index = 0; index < 1_024; index++)
                    {
                        archive.CreateEntry($"assets/{index}.bin");
                    }
                }

                var service = CreateService(rootPath);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn()));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task ExportAsync_CanceledBeforeWriting_ThrowsOperationCanceledException()
        {
            string rootPath = CreateTempRoot();
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.Cancel();
                var service = CreateService(rootPath);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.ExportAsync(
                        Path.Combine(rootPath, "canceled.ivproject"),
                        CreateSnapshot(null, []),
                        RoiPluginRegistry.CreateBuiltIn(),
                        cancellationTokenSource.Token));
                    Assert.False(File.Exists(Path.Combine(rootPath, "canceled.ivproject")));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_CaseVariantImagePath_IsRejected()
        {
            // 反序列化大小写不敏感，而校验曾是大小写敏感的：小写 imagepath 能绕过 assets/ 限制加载包外文件。
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "evil.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    WriteSessionEntry(archive, """
                        { "SessionName": "evil", "imagepath": "../../secret.png" }
                        """);
                }

                var service = CreateService(rootPath);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn()));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_ImagePathMissingFromPackage_IsRejected()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "missing-asset.ivproject");
                using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
                {
                    WriteSessionEntry(archive, """
                        { "ImagePath": "assets/ghost.png" }
                        """);
                }

                var service = CreateService(rootPath);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.LoadAsync(packagePath, RoiPluginRegistry.CreateBuiltIn()));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_SamePathDifferentContent_DoesNotReuseStaleAssets()
        {
            string rootPath = CreateTempRoot();
            try
            {
                string packagePath = Path.Combine(rootPath, "sample.ivproject");
                var service = CreateService(rootPath);
                var registry = RoiPluginRegistry.CreateBuiltIn();

                // 第一个包：assets/a.png 内容为 "AAA"。
                string imageA = Path.Combine(rootPath, "a.png");
                await File.WriteAllTextAsync(imageA, "AAA");
                await service.ExportAsync(packagePath, CreateSnapshot(imageA, []), registry);
                ImageViewerSessionData first = await service.LoadAsync(packagePath, registry);
                Assert.EndsWith("a.png", first.ImagePath, StringComparison.Ordinal);
                Assert.Equal("AAA", await File.ReadAllTextAsync(first.ImagePath!));

                // 同一路径写入内容不同的第二个包：缓存键随内容变化，旧残留不得参与加载。
                string imageB = Path.Combine(rootPath, "b.png");
                await File.WriteAllTextAsync(imageB, "BBB");
                await service.ExportAsync(packagePath, CreateSnapshot(imageB, []), registry);
                ImageViewerSessionData second = await service.LoadAsync(packagePath, registry);
                Assert.EndsWith("b.png", second.ImagePath, StringComparison.Ordinal);
                Assert.Equal("BBB", await File.ReadAllTextAsync(second.ImagePath!));
                Assert.NotEqual(
                    Path.GetDirectoryName(first.ImagePath),
                    Path.GetDirectoryName(second.ImagePath));
            }
            finally
            {
                DeleteTempRoot(rootPath);
            }
        }

        [Fact]
        public async Task LoadAsync_IdenticalPackageAtAnotherPathReusesExtractedAssets()
        {
            string root = CreateTempRoot();
            try
            {
                var registry = RoiPluginRegistry.CreateBuiltIn();
                var service = CreateService(root);
                string image = Path.Combine(root, "image.png");
                string package = Path.Combine(root, "original.ivpkg");
                string copy = Path.Combine(root, "copy.ivpkg");
                await File.WriteAllTextAsync(image, "image");
                await service.ExportAsync(package, CreateSnapshot(image, []), registry);
                File.Copy(package, copy);
                ImageViewerSessionData first = await service.LoadAsync(package, registry);
                var oldTimestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(first.ImagePath!, oldTimestamp);
                ImageViewerSessionData second = await service.LoadAsync(copy, registry);
                Assert.Equal(first.ImagePath, second.ImagePath);
                Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(second.ImagePath!));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task SaveStandaloneSession_CopiesCachedImageAndSurvivesEvictionAndMoving()
        {
            string root = CreateTempRoot();
            try
            {
                string cache = Path.Combine(root, "cache");
                string entry = MakeCacheEntry(cache, 1);
                string image = Path.Combine(entry, "assets", "image.png");
                string savedFolder = Path.Combine(root, "saved");
                string session = Path.Combine(savedFolder, "first.ivsession");
                string another = Path.Combine(savedFolder, "second.ivsession");
                var registry = RoiPluginRegistry.CreateBuiltIn();
                var service = new ImageViewerSessionService();

                await service.SaveToFileAsync(session, CreateSnapshot(image, []), registry);
                service.SaveToFile(another, CreateSnapshot(image, []), registry);

                string asset = Assert.Single(Directory.GetFiles(Path.Combine(savedFolder, "ImageViewer.assets")));
                Assert.Equal(asset, (await service.LoadFromFileAsync(session, registry)).ImagePath);
                Assert.Equal(asset, (await service.LoadFromFileAsync(another, registry)).ImagePath);
                Assert.Contains("ImageViewer.assets", await File.ReadAllTextAsync(session));
                using (FileStream guard = await ImageViewerProjectPackageCache.LockAsync(cache, default))
                {
                    ImageViewerProjectPackageCache.Trim(cache, string.Empty, Path.Combine(root, "recovery"), 1);
                }
                Assert.False(Directory.Exists(entry));

                string movedFolder = Path.Combine(root, "moved");
                Directory.Move(savedFolder, movedFolder);
                ImageViewerSessionData moved = await service.LoadFromFileAsync(Path.Combine(movedFolder, "first.ivsession"), registry);
                Assert.Equal("0123456789", await File.ReadAllTextAsync(moved.ImagePath!));

                string saveAs = Path.Combine(root, "other", "copy.ivsession");
                await service.SaveToFileAsync(saveAs, CreateSnapshot(moved.ImagePath, []), registry);
                ImageViewerSessionData copied = await service.LoadFromFileAsync(saveAs, registry);
                Assert.StartsWith(Path.GetDirectoryName(saveAs)!, copied.ImagePath!, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("0123456789", await File.ReadAllTextAsync(copied.ImagePath!));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task Trim_EvictsLeastRecentlyUsedAndKeepsLiveAndRecoveryAssets()
        {
            string root = CreateTempRoot();
            try
            {
                string cache = Path.Combine(root, "cache");
                string recovery = Path.Combine(root, "recovery");
                Directory.CreateDirectory(recovery);
                using FileStream guard = await ImageViewerProjectPackageCache.LockAsync(cache, default);
                string current = MakeCacheEntry(cache, 1);
                string live = MakeCacheEntry(cache, 2);
                string recovered = MakeCacheEntry(cache, 3);
                string older = MakeCacheEntry(cache, 4);
                string newer = MakeCacheEntry(cache, 5);
                File.SetLastWriteTimeUtc(ImageViewerProjectPackageCache.MarkerPath(older), DateTime.UtcNow.AddDays(-3));
                File.SetLastWriteTimeUtc(ImageViewerProjectPackageCache.MarkerPath(newer), DateTime.UtcNow.AddDays(-2));
                using FileStream liveLease = ImageViewerProjectPackageCache.LeaseEntry(live);
                string recoveryFile = Path.Combine(recovery, "autosave.ivsession");
                string recoveryAsset = Path.Combine(recovered, "assets", "image.png");
                File.WriteAllText(recoveryFile, System.Text.Json.JsonSerializer.Serialize(new { ImagePath = recoveryAsset }));

                ImageViewerProjectPackageCache.Trim(cache, current, recovery, 40);
                Assert.False(Directory.Exists(older));
                Assert.True(Directory.Exists(newer));

                ImageViewerProjectPackageCache.Trim(cache, current, recovery, 10);
                Assert.False(Directory.Exists(newer));
                Assert.True(Directory.Exists(current));
                Assert.True(Directory.Exists(live));
                Assert.True(File.Exists(recoveryAsset));

                liveLease.Dispose();
                File.Delete(recoveryFile);
                ImageViewerProjectPackageCache.Trim(cache, current, recovery, 10);
                Assert.False(Directory.Exists(live));
                Assert.False(Directory.Exists(recovered));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        private static string MakeCacheEntry(string cache, byte id)
        {
            byte[] hash = new byte[32];
            hash[0] = id;
            string entry = ImageViewerProjectPackageCache.GetDirectory(cache, hash);
            string assets = Path.Combine(entry, "assets");
            Directory.CreateDirectory(assets);
            File.WriteAllText(Path.Combine(assets, "image.png"), "0123456789");
            ImageViewerProjectPackageCache.Complete(entry, 10);
            return entry;
        }

        private static void WriteSessionEntry(ZipArchive archive, string sessionJson)
        {
            ZipArchiveEntry entry = archive.CreateEntry("session.ivsession");
            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(sessionJson);
        }

        /// <summary>
        /// 构造快照时先完成 ROI→DTO 转换，与 ImageViewerSessionPersistenceWorkflow.CaptureSnapshot 一致。
        /// </summary>
        private static ImageViewerPersistenceSnapshot CreateSnapshot(
            string? imagePath,
            IReadOnlyList<RoiBase> rois,
            double pixelSize = 1.0,
            string physicalUnit = "px")
        {
            return new ImageViewerPersistenceSnapshot(
                imagePath,
                RoiPersistenceService.CreateDocument(
                    rois,
                    pixelSize,
                    physicalUnit,
                    RoiPluginRegistry.CreateBuiltIn()),
                1.0,
                0,
                0,
                null);
        }

        private static ImageViewerProjectPackageService CreateService(string rootPath)
        {
            var sessionStoragePolicy = new LocalAppDataImageViewerSessionStoragePolicy(
                Path.Combine(rootPath, "cache"),
                TimeSpan.FromMinutes(1));
            var sessionService = new ImageViewerSessionService();
            return new ImageViewerProjectPackageService(sessionService, sessionStoragePolicy);
        }

        private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private static string CreateTempRoot()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"ImageViewerTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            return rootPath;
        }

        private static void DeleteTempRoot(string rootPath)
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
