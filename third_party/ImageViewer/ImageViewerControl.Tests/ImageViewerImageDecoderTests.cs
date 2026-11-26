using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImageViewerControl.Tests
{
    public sealed class ImageViewerImageDecoderTests
    {
        private const uint Red = 0xFFFF0000;
        private const uint Green = 0xFF00FF00;
        private const uint Blue = 0xFF0000FF;
        private const uint White = 0xFFFFFFFF;
        private const uint Black = 0xFF000000;
        private const uint Yellow = 0xFF00FFFF;

        [Fact]
        public async Task BatchDecode_BlocksReplacementAfterHashingAndReleasesFileAfterDecoding()
        {
            string path = Path.Combine(Path.GetTempPath(), $"imageviewer-batch-input-{Guid.NewGuid():N}.png");
            string replacement = path + ".replacement";
            try
            {
                WpfTestRunner.Run(() => SaveGray16Slice(path, 3, 1234));
                byte[] original = await File.ReadAllBytesAsync(path);
                string sha256 = Convert.ToHexString(SHA256.HashData(original));
                ImageViewerImageDecoder.ImageFileTrace? captured = null;
                BitmapSource bitmap = await ImageViewerImageDecoder.LoadBatchImageAsync(path, trace =>
                {
                    captured = trace;
                    Assert.Throws<IOException>(() => File.Move(path, replacement));
                }, CancellationToken.None);

                Assert.Equal(sha256, captured?.Sha256);
                Assert.Equal(original.LongLength, captured?.Length);
                Assert.Equal(File.GetLastWriteTimeUtc(path), captured?.LastWriteTimeUtc);
                var pixels = new ushort[12];
                bitmap.CopyPixels(pixels, 6, 0);
                Assert.All(pixels, pixel => Assert.Equal((ushort)1234, pixel));
                File.Move(path, replacement);
            }
            finally
            {
                File.Delete(path);
                File.Delete(replacement);
            }
        }

        [Fact]
        public async Task VolumePreflight_ChecksAllDimensionsAndBudgetsForDecodedFormat()
        {
            string first = Path.Combine(Path.GetTempPath(), $"volume-first-{Guid.NewGuid():N}.png");
            string second = Path.Combine(Path.GetTempPath(), $"volume-second-{Guid.NewGuid():N}.png");
            try
            {
                WpfTestRunner.Run(() =>
                {
                    SaveGray16Slice(first, 3);
                    SaveGray16Slice(second, 3);
                });
                VolumeImportEstimate estimate = await VolumeImportPreflight.InspectAsync([first, second]);
                Assert.Equal((3, 4, 64L, 96L), (estimate.Width, estimate.Height, estimate.DecodedBytes, estimate.EstimatedBytes));
                Assert.True(estimate.ExceedsBudget(95));
                Assert.False(estimate.ExceedsBudget(96));

                WpfTestRunner.Run(() => SaveGray16Slice(second, 5));
                InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => VolumeImportPreflight.InspectAsync([first, second]));
                Assert.Contains(Path.GetFileName(second), error.Message);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => VolumeImportPreflight.InspectAsync([first, second], new CancellationToken(true)));
            }
            finally
            {
                File.Delete(first);
                File.Delete(second);
            }
        }

        private static void SaveGray16Slice(string path, int width, ushort intensity = 0)
        {
            const int height = 4;
            int stride = width * 2;
            var pixels = new ushort[height * width];
            Array.Fill(pixels, intensity);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(
                BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, stride)));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        public static IEnumerable<object[]> OrientationCases()
        {
            yield return new object[] { 1, new[] { Red, Green, Blue, White, Black, Yellow }, 2, 3 };
            yield return new object[] { 2, new[] { Green, Red, White, Blue, Yellow, Black }, 2, 3 };
            yield return new object[] { 3, new[] { Yellow, Black, White, Blue, Green, Red }, 2, 3 };
            yield return new object[] { 4, new[] { Black, Yellow, Blue, White, Red, Green }, 2, 3 };
            yield return new object[] { 5, new[] { Red, Blue, Black, Green, White, Yellow }, 3, 2 };
            yield return new object[] { 6, new[] { Black, Blue, Red, Yellow, White, Green }, 3, 2 };
            yield return new object[] { 7, new[] { Yellow, White, Green, Black, Blue, Red }, 3, 2 };
            yield return new object[] { 8, new[] { Green, White, Yellow, Red, Blue, Black }, 3, 2 };
        }

        [Theory]
        [MemberData(nameof(OrientationCases))]
        public void Apply_MapsPixelsForEveryExifOrientation(int orientation, uint[] expectedPixels, int expectedWidth, int expectedHeight)
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource source = CreateSource();
                BitmapSource result = ExifOrientationTransform.Apply(source, orientation);

                Assert.Equal(expectedWidth, result.PixelWidth);
                Assert.Equal(expectedHeight, result.PixelHeight);
                Assert.Equal(expectedPixels, ReadPixels(result));
            });
        }

        [Fact]
        public async Task VerifiedVolumeImport_RejectsChangedBytesEvenWhenLengthAndTimestampMatch()
        {
            string first = Path.Combine(Path.GetTempPath(), $"volume-first-{Guid.NewGuid():N}.png");
            string second = Path.Combine(Path.GetTempPath(), $"volume-second-{Guid.NewGuid():N}.png");
            try
            {
                WpfTestRunner.Run(() =>
                {
                    SaveGray16Slice(first, 3, 1000);
                    SaveGray16Slice(second, 3, 40000);
                });
                byte[] original = await File.ReadAllBytesAsync(second);
                byte[] padded = [.. original, 0];
                await File.WriteAllBytesAsync(second, padded);
                DateTime originalWriteTime = File.GetLastWriteTimeUtc(second);

                VolumeImportPlan plan = await VolumeImportPreflight.PrepareAsync([first, second]);
                var progress = new SliceLoadProgress();
                BitmapSource[] loaded = await plan.LoadAllSlicesAsync(progress);
                Assert.Collection(progress.CompletedCounts, count => Assert.Equal(1, count), count => Assert.Equal(2, count));
                Assert.Collection(loaded, slice => AssertSlice(slice, 1000), slice => AssertSlice(slice, 40000));

                padded[^1] = 1;
                await File.WriteAllBytesAsync(second, padded);
                File.SetLastWriteTimeUtc(second, originalWriteTime);
                Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(second));
                Assert.Equal(padded.Length, new FileInfo(second).Length);

                InvalidDataException changed = await Assert.ThrowsAsync<InvalidDataException>(() => plan.LoadAllSlicesAsync());
                Assert.IsType<ImageViewerSessionImageMismatchException>(changed.InnerException);
                Assert.Contains(Path.GetFileName(second), changed.Message, StringComparison.Ordinal);

                File.Delete(second);
                InvalidDataException missing = await Assert.ThrowsAsync<InvalidDataException>(() => plan.LoadSliceAsync(1));
                Assert.IsType<FileNotFoundException>(missing.InnerException);
                Assert.Contains(Path.GetFileName(second), missing.Message, StringComparison.Ordinal);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.LoadSliceAsync(0, new CancellationToken(true)));
            }
            finally
            {
                File.Delete(first);
                File.Delete(second);
            }

            static void AssertSlice(BitmapSource slice, ushort expected)
            {
                Assert.True(slice.IsFrozen);
                Assert.Equal(PixelFormats.Gray16, slice.Format);
                Assert.Equal((3, 4), (slice.PixelWidth, slice.PixelHeight));
                var pixels = new ushort[12];
                slice.CopyPixels(pixels, 6, 0);
                Assert.All(pixels, intensity => Assert.Equal(expected, intensity));
            }
        }

        private sealed class SliceLoadProgress : IProgress<int>
        {
            public List<int> CompletedCounts { get; } = [];

            public void Report(int value)
            {
                lock (CompletedCounts)
                {
                    CompletedCounts.Add(value);
                }
            }
        }

        [Fact]
        public void VerifiedVolumeImport_SessionKeepsStoredGridForExifRotatedSlice()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), $"imageviewer-volume-exif-{Guid.NewGuid():N}");
                Directory.CreateDirectory(root);
                try
                {
                    string first = Path.Combine(root, "first.jpg");
                    string second = Path.Combine(root, "second.jpg");
                    string sessionPath = Path.Combine(root, "slice.ivsession");
                    WriteJpegWithOrientation(first, 6);
                    File.Copy(first, second);
                    VolumeImportPlan plan = await VolumeImportPreflight.PrepareAsync([first, second]);
                    BitmapSource slice = await plan.LoadSliceAsync(0);
                    var image = ImageViewerSnapshotImage.Capture(
                        ImageViewerImageSourceRegistry.TryGetPath(slice, out string sourcePath) ? sourcePath : null,
                        () => slice);
                    var registry = ImageViewer.Plugins.RoiPluginRegistry.CreateBuiltIn();
                    var snapshot = new ImageViewerPersistenceSnapshot(image.Path,
                        RoiPersistenceService.CreateDocument([], 1, "px", registry), 1, 0, 0, null)
                    {
                        InMemoryImage = image.Bitmap,
                        ImageSha256 = image.Sha256
                    };
                    var service = new ImageViewerSessionService();
                    await service.SaveToFileAsync(sessionPath, snapshot, registry);
                    string savedImagePath = (await service.LoadFromFileAsync(sessionPath, registry)).ImagePath!;
                    BitmapSource reopened = await ImageViewerImageDecoder.LoadFromFileAsync(savedImagePath);

                    Assert.Equal((2, 3), (reopened.PixelWidth, reopened.PixelHeight));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void Apply_LeavesUnknownOrientationUnchanged()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource source = CreateSource();
                BitmapSource result = ExifOrientationTransform.Apply(source, 0);

                Assert.Same(source, result);
            });
        }

        [Fact]
        public void EnsureWithinLimits_AllowsBoundary()
        {
            ImageViewerImageDecoder.EnsureWithinLimits(100, 100, 10_000);
        }

        [Fact]
        public void EnsureWithinLimits_ThrowsWhenBudgetExceeded()
        {
            ImageViewerImageTooLargeException exception = Assert.Throws<ImageViewerImageTooLargeException>(
                () => ImageViewerImageDecoder.EnsureWithinLimits(101, 100, 10_000));

            Assert.Equal(101, exception.PixelWidth);
            Assert.Equal(100, exception.PixelHeight);
            Assert.Equal(10_100, exception.PixelCount);
            Assert.Equal(10_000, exception.MaxPixelCount);
        }

        [Fact]
        public void LoadFromFile_AppliesExifOrientationAndRegistersSourcePath()
        {
            WpfTestRunner.Run(() =>
            {
                string filePath = Path.Combine(Path.GetTempPath(), $"imageviewer-exif-{Guid.NewGuid():N}.jpg");
                try
                {
                    WriteJpegWithOrientation(filePath, 6);

                    BitmapSource loaded = ImageViewerImageDecoder.LoadFromFile(filePath, CancellationToken.None);

                    Assert.Equal(3, loaded.PixelWidth);
                    Assert.Equal(2, loaded.PixelHeight);
                    Assert.True(ImageViewerImageSourceRegistry.TryGetPath(loaded, out string registeredPath));
                    Assert.Equal(filePath, registeredPath);
                }
                finally
                {
                    File.Delete(filePath);
                }
            });
        }

        [Fact]
        public void ProjectAndBatchUseTheSameExifCoordinates()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), $"imageviewer-exif-workflows-{Guid.NewGuid():N}");
                Directory.CreateDirectory(root);
                try
                {
                    string imagePath = Path.Combine(root, "rotated.jpg");
                    string sessionPath = Path.Combine(root, "project.ivsession");
                    string csvPath = Path.Combine(root, "batch.csv");
                    WriteJpegWithOrientation(imagePath, 6);
                    File.WriteAllText(sessionPath, """{"Version":2,"ImagePath":"rotated.jpg","Scale":1}""");

                    using ServiceProvider provider = new ServiceCollection()
                        .AddSingleton<IImageViewerSessionStoragePolicy>(new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromSeconds(30)))
                        .AddImageViewerHost()
                        .BuildServiceProvider();
                    using IServiceScope scope = provider.CreateScope();
                    var viewer = scope.ServiceProvider.GetRequiredService<ImageViewer.Controls.ImageViewer>();
                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));
                    var bitmap = Assert.IsAssignableFrom<BitmapSource>(viewer.ImageSource);
                    Assert.Equal((3, 2), (bitmap.PixelWidth, bitmap.PixelHeight));

                    var roi = new CircleRoi { Center = new PointD(2.5, 0.5), Radius = 0.5 };
                    Assert.True(ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out RoiStatistics statistics));
                    await BatchRoiAnalysisExportService.ExportAsync(csvPath, [imagePath], [roi], 1, "px");
                    string[] cells = (await File.ReadAllLinesAsync(csvPath))[1].Split(',');
                    Assert.Equal(statistics.Mean, double.Parse(cells[8], System.Globalization.CultureInfo.InvariantCulture), 2);
                    Assert.Equal("1", cells[12]);

                    BitmapSource rawSlice = await ImageViewerImageDecoder.LoadFromFileAsync(imagePath, applyExifOrientation: false);
                    Assert.Equal((2, 3), (rawSlice.PixelWidth, rawSlice.PixelHeight));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public async Task BatchExport_PreservesGray16Statistics()
        {
            string root = Path.Combine(Path.GetTempPath(), $"imageviewer-gray16-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string imagePath = Path.Combine(root, "gray16.tiff");
                string csvPath = Path.Combine(root, "batch.csv");
                const ushort intensity = 4096;
                ushort[] pixels = [intensity, intensity, intensity, intensity];
                BitmapSource source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, pixels, 4);
                var encoder = new TiffBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (FileStream stream = File.Create(imagePath))
                {
                    encoder.Save(stream);
                }

                BitmapSource bitmap = await ImageViewerImageDecoder.LoadFromFileAsync(imagePath);
                Assert.Equal(PixelFormats.Gray16, bitmap.Format);
                Assert.True(bitmap.IsFrozen);

                var roi = new CircleRoi { Center = new PointD(1, 1), Radius = 1 };
                await BatchRoiAnalysisExportService.ExportAsync(csvPath, [imagePath], [roi], 1, "px");
                Assert.Contains(",4096.00,4096,4096,0.00,4,", (await File.ReadAllLinesAsync(csvPath))[1]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static BitmapSource CreateSource()
        {
            uint[] pixels = { Red, Green, Blue, White, Black, Yellow };
            BitmapSource source = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 2 * 4);
            source.Freeze();
            return source;
        }

        private static uint[] ReadPixels(BitmapSource bitmap)
        {
            var pixels = new uint[bitmap.PixelWidth * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * sizeof(uint), 0);
            return pixels;
        }

        private static void WriteJpegWithOrientation(string filePath, ushort orientation)
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);

            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(CreateSource(), null, metadata, null));
            using FileStream stream = File.Create(filePath);
            encoder.Save(stream);
        }
    }
}
