using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerExportArtifactSetTests
    {
        [Fact]
        public async Task WriteAllTextAsync_OverwritesSetAndLeavesNoTemporaryFiles()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                string report = Path.Combine(root, "export.report.md");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                File.WriteAllText(report, "old report");

                await ImageViewerExportArtifactSet.WriteAllTextAsync(new Dictionary<string, string>
                {
                    [csv] = "new csv",
                    [metadata] = "new metadata",
                    [report] = "new report"
                });

                Assert.Equal("new csv", File.ReadAllText(csv));
                Assert.Equal("new metadata", File.ReadAllText(metadata));
                Assert.Equal("new report", File.ReadAllText(report));
                Assert.Equal(3, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_RestoresPreviousCompleteSetWhenReplacementFails()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                string report = Path.Combine(root, "export.report.md");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                File.WriteAllText(report, "old report");
                var fileSystem = new FaultingFileSystem(failOnReplaceCall: 2);

                await Assert.ThrowsAsync<IOException>(() => ImageViewerExportArtifactSet.WriteAllBytesAsync(
                    new Dictionary<string, byte[]>
                    {
                        [csv] = [1],
                        [metadata] = [2],
                        [report] = [3]
                    },
                    fileSystem: fileSystem));

                Assert.Equal("old csv", File.ReadAllText(csv));
                Assert.Equal("old metadata", File.ReadAllText(metadata));
                Assert.Equal("old report", File.ReadAllText(report));
                Assert.Equal(3, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_RemovesNewTargetsWhenLaterMoveFails()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                var fileSystem = new FaultingFileSystem(failOnMoveCall: 2);

                await Assert.ThrowsAsync<IOException>(() => ImageViewerExportArtifactSet.WriteAllBytesAsync(
                    new Dictionary<string, byte[]>
                    {
                        [csv] = [1],
                        [metadata] = [2]
                    },
                    fileSystem: fileSystem));

                Assert.False(File.Exists(csv));
                Assert.False(File.Exists(metadata));
                Assert.Empty(Directory.GetFiles(root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_CancellationDuringStagingLeavesOldFilesAndNoResidue()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                using var cancellation = new CancellationTokenSource();
                var fileSystem = new FaultingFileSystem(cancelAfterStageCount: 1, cancellation: cancellation);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageViewerExportArtifactSet.WriteAllBytesAsync(
                    new Dictionary<string, byte[]>
                    {
                        [csv] = [1],
                        [metadata] = [2]
                    },
                    fileSystem,
                    cancellation.Token));

                Assert.Equal("old csv", File.ReadAllText(csv));
                Assert.Equal("old metadata", File.ReadAllText(metadata));
                Assert.Equal(2, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_StagingFailureLeavesOldFilesAndNoResidue()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                var fileSystem = new FaultingFileSystem(failOnStageCount: 2);

                await Assert.ThrowsAsync<IOException>(() => ImageViewerExportArtifactSet.WriteAllBytesAsync(
                    new Dictionary<string, byte[]>
                    {
                        [csv] = [1],
                        [metadata] = [2]
                    },
                    fileSystem: fileSystem));

                Assert.Equal("old csv", File.ReadAllText(csv));
                Assert.Equal("old metadata", File.ReadAllText(metadata));
                Assert.Equal(2, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_CancellationBetweenCommitsRestoresPreviousCompleteSet()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                using var cancellation = new CancellationTokenSource();
                var fileSystem = new FaultingFileSystem(cancelAfterReplaceCount: 1, cancellation: cancellation);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageViewerExportArtifactSet.WriteAllBytesAsync(
                    new Dictionary<string, byte[]>
                    {
                        [csv] = [1],
                        [metadata] = [2]
                    },
                    fileSystem,
                    cancellation.Token));

                Assert.Equal("old csv", File.ReadAllText(csv));
                Assert.Equal("old metadata", File.ReadAllText(metadata));
                Assert.Equal(2, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAllTextAsync_SerializesConcurrentExportsOfTheSameFiles()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                string metadata = Path.Combine(root, "export.metadata.json");
                File.WriteAllText(csv, "old csv");
                File.WriteAllText(metadata, "old metadata");
                using var releaseFirstCommit = new ManualResetEventSlim();
                var firstCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var firstFileSystem = new BlockingFileSystem(firstCommitEntered, releaseFirstCommit);
                Task first = ImageViewerExportArtifactSet.WriteAllBytesAsync(new Dictionary<string, byte[]>
                {
                    [csv] = System.Text.Encoding.UTF8.GetBytes("first csv"),
                    [metadata] = System.Text.Encoding.UTF8.GetBytes("first metadata")
                }, fileSystem: firstFileSystem);
                await firstCommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var secondFileSystem = new StageSignalingFileSystem();
                Task second = ImageViewerExportArtifactSet.WriteAllBytesAsync(new Dictionary<string, byte[]>
                {
                    [csv] = System.Text.Encoding.UTF8.GetBytes("second csv"),
                    [metadata] = System.Text.Encoding.UTF8.GetBytes("second metadata")
                }, fileSystem: secondFileSystem);
                try
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => secondFileSystem.FirstStage.WaitAsync(TimeSpan.FromMilliseconds(100)));
                }
                finally
                {
                    releaseFirstCommit.Set();
                }

                await Task.WhenAll(first, second);

                Assert.Equal("second csv", File.ReadAllText(csv));
                Assert.Equal("second metadata", File.ReadAllText(metadata));
                Assert.Equal(2, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task SaveCsvAsync_ProducesCoherentMetadataAndReportHashesOnOverwrite()
        {
            string root = CreateTempDirectory();
            try
            {
                string csv = Path.Combine(root, "export.csv");
                var roi = new CircleRoi { Label = "target", Center = new PointD(4, 5), Radius = 2 };
                var context = new RoiAnalysisExportContext(SourcePath: null, PluginRegistry: null);

                await RoiAnalysisExportService.SaveCsvAsync(csv, [roi], bitmap: null, pixelSize: 1, physicalUnit: null, calibration: null, exportContext: context);
                string metadataPath = Path.ChangeExtension(csv, ".metadata.json");
                string reportPath = Path.ChangeExtension(csv, ".report.md");
                using JsonDocument firstMetadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                string firstBatchId = firstMetadata.RootElement.GetProperty("exportBatchId").GetString()!;
                JsonElement firstArtifacts = firstMetadata.RootElement.GetProperty("artifactSet");
                Assert.Equal(firstBatchId, firstArtifacts.GetProperty("batchId").GetString());
                Assert.Equal(HashFile(csv), firstArtifacts.GetProperty("csvSha256").GetString());
                Assert.Equal(HashFile(reportPath), firstArtifacts.GetProperty("reportSha256").GetString());
                Assert.Contains($"exportBatchId: {firstBatchId}", File.ReadAllText(reportPath));

                await RoiAnalysisExportService.SaveCsvAsync(csv, [roi], bitmap: null, pixelSize: 1, physicalUnit: null, calibration: null, exportContext: context);
                using JsonDocument secondMetadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                string secondBatchId = secondMetadata.RootElement.GetProperty("exportBatchId").GetString()!;
                Assert.NotEqual(firstBatchId, secondBatchId);
                Assert.Equal(HashFile(csv), secondMetadata.RootElement.GetProperty("artifactSet").GetProperty("csvSha256").GetString());
                Assert.Equal(HashFile(reportPath), secondMetadata.RootElement.GetProperty("artifactSet").GetProperty("reportSha256").GetString());
                Assert.Equal(3, Directory.GetFiles(root).Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SaveCsvAsync_UsesTheSameCapturedInputsForEveryArtifact()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempDirectory();
                try
                {
                    string csv = Path.Combine(root, "export.csv");
                    string unrelatedPath = Path.Combine(root, "unverified.png");
                    await File.WriteAllTextAsync(unrelatedPath, "unrelated file");
                    var roi = new CircleRoi { Label = "snapshot-before", Center = new PointD(2, 2), Radius = 2 };
                    var bitmap = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Gray8, null);
                    byte[] originalPixels = Enumerable.Repeat((byte)11, 16).ToArray();
                    bitmap.WritePixels(new Int32Rect(0, 0, 4, 4), originalPixels, 4, 0);
                    var calibration = new CameraCalibration { CalibrationId = "calibration-before" };
                    var settings = new Dictionary<string, string> { ["window"] = "before" };
                    var context = new RoiAnalysisExportContext(unrelatedPath, RoiPluginRegistry.CreateBuiltIn(), settings);

                    Task export = RoiAnalysisExportService.SaveCsvAsync(csv, [roi], bitmap, 1, "mm", calibration, context);
                    roi.Label = "snapshot-after";
                    calibration.CalibrationId = "calibration-after";
                    settings["window"] = "after";
                    bitmap.WritePixels(new Int32Rect(0, 0, 4, 4), new byte[16], 4, 0);
                    await export;

                    string csvText = await File.ReadAllTextAsync(csv);
                    string report = await File.ReadAllTextAsync(Path.ChangeExtension(csv, ".report.md"));
                    using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csv, ".metadata.json")));
                    JsonElement result = metadata.RootElement;
                    JsonElement source = result.GetProperty("source");
                    string pixelHash = Convert.ToHexString(SHA256.HashData(originalPixels)).ToLowerInvariant();
                    Assert.Contains("snapshot-before", csvText, StringComparison.Ordinal);
                    Assert.Contains(",11.00,11,11,0.00,", csvText, StringComparison.Ordinal);
                    Assert.Contains("snapshot-before", report, StringComparison.Ordinal);
                    Assert.Contains(pixelHash[..12], report, StringComparison.Ordinal);
                    Assert.Equal("snapshot-before", result.GetProperty("result").GetProperty("inspectionResults")[0].GetProperty("label").GetString());
                    Assert.Contains("snapshot-before", result.GetProperty("roiDocument").GetRawText(), StringComparison.Ordinal);
                    Assert.Equal("calibration-before", result.GetProperty("calibration").GetProperty("camera").GetProperty(nameof(CameraCalibration.CalibrationId)).GetString());
                    Assert.Equal("before", result.GetProperty("renderSettings").GetProperty("window").GetString());
                    Assert.Equal(JsonValueKind.Null, source.GetProperty("sha256").ValueKind);
                    Assert.Equal(pixelHash, source.GetProperty("pixelBufferSha256").GetString());
                    Assert.Equal("Gray8", source.GetProperty("pixelBufferFormat").GetString());
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public async Task SaveCsvAsync_UsesDecodedFileTraceWhenThePathIsOverwritten()
        {
            string root = CreateTempDirectory();
            try
            {
                string path = Path.Combine(root, "source.png");
                WpfTestRunner.Run(() =>
                {
                    var encoder = new PngBitmapEncoder();
                    var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, new byte[] { 1, 2, 3, 4 }, 2);
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using FileStream stream = File.Create(path);
                    encoder.Save(stream);
                });
                string expectedHash = HashFile(path);
                long expectedLength = new FileInfo(path).Length;
                DateTime expectedWriteTime = File.GetLastWriteTimeUtc(path);
                BitmapSource original = await ImageViewerImageDecoder.LoadDocumentImageAsync(path, null, null, CancellationToken.None);
                await File.WriteAllTextAsync(path, "replacement");

                string csv = Path.Combine(root, "export.csv");
                var context = new RoiAnalysisExportContext(path, null);
                await RoiAnalysisExportService.SaveCsvAsync(csv, [], original, 1, "mm", null, context);

                using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csv, ".metadata.json")));
                JsonElement source = metadata.RootElement.GetProperty("source");
                Assert.Equal(expectedHash, source.GetProperty("sha256").GetString());
                Assert.Equal(expectedLength, source.GetProperty("lengthBytes").GetInt64());
                Assert.Equal(expectedWriteTime, source.GetProperty("lastWriteTimeUtc").GetDateTime());
                Assert.Equal(JsonValueKind.Null, source.GetProperty("pixelBufferSha256").ValueKind);
                string report = await File.ReadAllTextAsync(Path.ChangeExtension(csv, ".report.md"));
                Assert.Contains(expectedHash[..12], report, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), $"image-viewer-artifact-set-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string HashFile(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private sealed class FaultingFileSystem(
            int? failOnReplaceCall = null,
            int? cancelAfterStageCount = null,
            int? failOnStageCount = null,
            int? failOnMoveCall = null,
            int? cancelAfterReplaceCount = null,
            CancellationTokenSource? cancellation = null) : IImageViewerExportArtifactFileSystem
        {
            private int _replaceCalls;
            private int _stageCalls;
            private int _moveCalls;
            private readonly IImageViewerExportArtifactFileSystem _inner = PhysicalImageViewerExportArtifactFileSystem.Instance;

            public void CreateDirectory(string path) => _inner.CreateDirectory(path);

            public async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken)
            {
                await _inner.WriteAllBytesAsync(path, contents, cancellationToken);
                int call = Interlocked.Increment(ref _stageCalls);
                if (call == failOnStageCount)
                {
                    throw new IOException("Injected staging failure.");
                }

                if (call == cancelAfterStageCount)
                {
                    cancellation!.Cancel();
                }
            }

            public bool FileExists(string path) => _inner.FileExists(path);
            public void Move(string sourcePath, string destinationPath)
            {
                if (Interlocked.Increment(ref _moveCalls) == failOnMoveCall)
                {
                    throw new IOException("Injected move failure.");
                }

                _inner.Move(sourcePath, destinationPath);
            }

            public void Replace(string sourcePath, string destinationPath, string? backupPath)
            {
                int call = Interlocked.Increment(ref _replaceCalls);
                if (call == failOnReplaceCall)
                {
                    throw new IOException("Injected replacement failure.");
                }

                _inner.Replace(sourcePath, destinationPath, backupPath);
                if (call == cancelAfterReplaceCount)
                {
                    cancellation!.Cancel();
                }
            }

            public void Delete(string path) => _inner.Delete(path);
            public void TryDelete(string? path) => _inner.TryDelete(path);
        }

        private sealed class BlockingFileSystem(
            TaskCompletionSource entered,
            ManualResetEventSlim release) : IImageViewerExportArtifactFileSystem
        {
            private readonly IImageViewerExportArtifactFileSystem _inner = PhysicalImageViewerExportArtifactFileSystem.Instance;
            private int _blocked;

            public void CreateDirectory(string path) => _inner.CreateDirectory(path);
            public Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken) => _inner.WriteAllBytesAsync(path, contents, cancellationToken);
            public bool FileExists(string path) => _inner.FileExists(path);
            public void Move(string sourcePath, string destinationPath) => _inner.Move(sourcePath, destinationPath);

            public void Replace(string sourcePath, string destinationPath, string? backupPath)
            {
                if (Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    entered.SetResult();
                    release.Wait();
                }

                _inner.Replace(sourcePath, destinationPath, backupPath);
            }

            public void Delete(string path) => _inner.Delete(path);
            public void TryDelete(string? path) => _inner.TryDelete(path);
        }

        private sealed class StageSignalingFileSystem : IImageViewerExportArtifactFileSystem
        {
            private readonly IImageViewerExportArtifactFileSystem _inner = PhysicalImageViewerExportArtifactFileSystem.Instance;
            private readonly TaskCompletionSource _firstStage = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task FirstStage => _firstStage.Task;

            public void CreateDirectory(string path) => _inner.CreateDirectory(path);

            public async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken)
            {
                await _inner.WriteAllBytesAsync(path, contents, cancellationToken);
                _firstStage.TrySetResult();
            }

            public bool FileExists(string path) => _inner.FileExists(path);
            public void Move(string sourcePath, string destinationPath) => _inner.Move(sourcePath, destinationPath);
            public void Replace(string sourcePath, string destinationPath, string? backupPath) => _inner.Replace(sourcePath, destinationPath, backupPath);
            public void Delete(string path) => _inner.Delete(path);
            public void TryDelete(string? path) => _inner.TryDelete(path);
        }
    }
}
