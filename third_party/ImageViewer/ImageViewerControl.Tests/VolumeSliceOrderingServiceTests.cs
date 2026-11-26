using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class VolumeSliceOrderingServiceTests
    {
        [Fact]
        public void OrderPaths_UsesNumericNaturalOrderForSliceNames()
        {
            var ordered = VolumeSliceOrderingService.OrderPaths([
                @"C:\scan\slice_10.png",
                @"C:\scan\slice_2.png",
                @"C:\scan\slice_1.png"]);

            Assert.Equal(
                [
                    @"C:\scan\slice_1.png",
                    @"C:\scan\slice_2.png",
                    @"C:\scan\slice_10.png"],
                ordered);
        }

        [Fact]
        public void HasOrderChanged_DetectsReorderingWithoutChangingEqualCase()
        {
            Assert.True(VolumeSliceOrderingService.HasOrderChanged(
                ["slice_1.png", "slice_10.png", "slice_2.png"],
                ["slice_1.png", "slice_2.png", "slice_10.png"]));
            Assert.False(VolumeSliceOrderingService.HasOrderChanged(
                ["slice_1.png", "slice_2.png"],
                ["SLICE_1.PNG", "slice_2.png"]));
        }

        [Fact]
        public void OrderPaths_RejectsDuplicatePathsInsteadOfSilentlyDroppingOne()
        {
            Assert.Throws<ArgumentException>(() => VolumeSliceOrderingService.OrderPaths([
                "slice_1.png",
                "slice_1.png"]));
        }

        [Fact]
        public void OrderDescriptors_PrefersPhysicalPositionOverFilename()
        {
            VolumeSliceOrderingResult result = VolumeSliceOrderingService.OrderDescriptors([
                new VolumeSliceDescriptor("slice_1.png", Position: 2.0),
                new VolumeSliceDescriptor("slice_2.png", Position: -1.0),
                new VolumeSliceDescriptor("slice_10.png", Position: 0.5)]);

            Assert.Equal(VolumeSliceOrderSource.ExplicitPosition, result.Source);
            Assert.True(result.IsAuthoritative);
            Assert.Equal(["slice_2.png", "slice_10.png", "slice_1.png"], result.OrderedPaths);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public async Task OrderPathsWithMetadataAsync_UsesSliceSidecars()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-order-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string first = Path.Combine(root, "slice_10.png");
                string second = Path.Combine(root, "slice_2.png");
                await File.WriteAllTextAsync(first + ".slice.json", "{\"zPosition\": 10}");
                await File.WriteAllTextAsync(second + ".slice.json", "{\"zPosition\": 2}");

                VolumeSliceOrderingResult result = await VolumeSliceOrderingService.OrderPathsWithMetadataAsync([first, second]);

                Assert.Equal(VolumeSliceOrderSource.ExplicitPosition, result.Source);
                Assert.Equal([second, first], result.OrderedPaths);
                Assert.Equal(8, VolumeSliceOrderingService.AnalyzePositionSpacing(result).Interval);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("""{"zPosition": "invalid", "spacingZ": -2}""")]
        public async Task OrderPathsWithMetadataAsync_ReportsUnusablePrimaryAndUsesLegacySidecar(string content)
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-order-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string later = Path.Combine(root, "slice_1.png");
                string earlier = Path.Combine(root, "slice_2.png");
                string unusable = earlier + ".slice.json";
                await File.WriteAllTextAsync(later + ".slice.json", """{"zPosition": 12}""");
                await File.WriteAllTextAsync(unusable, content);
                string fallback = Path.ChangeExtension(earlier, ".json");
                await File.WriteAllTextAsync(fallback, """{"zPosition": 10}""");

                VolumeSliceOrderingResult result = await VolumeSliceOrderingService.OrderPathsWithMetadataAsync([later, earlier]);

                Assert.Equal(VolumeSliceOrderSource.ExplicitPosition, result.Source);
                Assert.Equal([earlier, later], result.OrderedPaths);
                Assert.Equal(2, VolumeSliceOrderingService.AnalyzePositionSpacing(result).Interval);
                Assert.Contains(result.Warnings, warning => warning.Contains(unusable, StringComparison.Ordinal));
                Assert.Contains(result.Warnings, warning => warning.Contains(fallback, StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task OrderPathsWithMetadataAsync_IdentifiesInvalidFieldWhenUsingInstanceOrder()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-order-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string later = Path.Combine(root, "slice_1.png");
                string earlier = Path.Combine(root, "slice_2.png");
                await File.WriteAllTextAsync(later + ".slice.json", """{"zPosition": [], "instanceNumber": 2}""");
                await File.WriteAllTextAsync(earlier + ".slice.json", """{"zPosition": 3, "instanceNumber": 1}""");

                VolumeSliceOrderingResult result = await VolumeSliceOrderingService.OrderPathsWithMetadataAsync([later, earlier]);

                Assert.Equal(VolumeSliceOrderSource.InstanceNumber, result.Source);
                Assert.Equal([earlier, later], result.OrderedPaths);
                Assert.Contains(result.Warnings, warning => warning.Contains(later + ".slice.json", StringComparison.Ordinal) &&
                    warning.Contains("zPosition", StringComparison.Ordinal));
                Assert.Contains(result.Warnings, warning => warning.Contains(Path.GetFileName(later), StringComparison.Ordinal) &&
                    warning.Contains("空间位置", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData(1.0, 2.0, null, 1.0, false, false)]
        [InlineData(1.0001, 2.0, 1.0, 1.0, false, false)]
        [InlineData(1.0, 2.0, 0.5, 1.0, false, true)]
        [InlineData(1.0, 4.0, null, 2.0, true, false)]
        [InlineData(0.0, 2.0, null, 2.0, true, false)]
        [InlineData(0.0, 0.0, null, null, true, false)]
        [InlineData(null, 2.0, 0.5, null, false, false)]
        public void AnalyzePositionSpacing_IdentifiesUniformGapsAndDistinguishesApproximation(
            double? middle, double last, double? declared, double? expected, bool irregular, bool conflicts)
        {
            VolumeSliceOrderingResult order = VolumeSliceOrderingService.OrderDescriptors([
                new VolumeSliceDescriptor("last.png", Position: last, SpacingZ: declared),
                new VolumeSliceDescriptor("first.png", Position: 0),
                new VolumeSliceDescriptor("middle.png", Position: middle)]);

            VolumeSlicePositionSpacing result = VolumeSliceOrderingService.AnalyzePositionSpacing(order);

            if (expected is { } interval)
            {
                Assert.Equal(interval, Assert.IsType<double>(result.Interval), precision: 8);
            }
            else
            {
                Assert.Null(result.Interval);
            }
            Assert.Equal(irregular, result.HasIrregularIntervals);
            Assert.Equal(conflicts, result.ConflictsWithDeclaredSpacing);
        }

        [Fact]
        public void OrderDescriptors_WarnsWhenFallingBackToFilename()
        {
            VolumeSliceOrderingResult result = VolumeSliceOrderingService.OrderDescriptors([
                new VolumeSliceDescriptor("slice_10.png"),
                new VolumeSliceDescriptor("slice_2.png")]);

            Assert.Equal(VolumeSliceOrderSource.NaturalFileName, result.Source);
            Assert.False(result.IsAuthoritative);
            Assert.Contains(result.Warnings, warning => warning.Contains("元数据"));
        }
    }
}
