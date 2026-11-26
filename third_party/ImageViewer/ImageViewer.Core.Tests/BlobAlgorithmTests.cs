using System;
using System.Collections.Generic;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Direct unit tests for the managed blob analysis: threshold semantics (Otsu vs
    /// manual, light vs dark, Gray16 scaling), 8-connectivity, area filtering, ordering,
    /// and the computed region statistics.
    /// </summary>
    public class BlobAlgorithmTests
    {
        [Fact]
        public void Segment_ManualThreshold_BoundsAreInclusiveForLightAndDark()
        {
            // Values 10, 20, 30 with a threshold of 20.
            IntensityImage image = IntensityImageFactory.CreateGray8(3, 1, (x, _) => (byte)((x + 1) * 10));
            bool[] expectedLight = [false, true, true];
            bool[] expectedDark = [true, true, false];

            Assert.Equal(expectedLight, BlobAlgorithm.Segment(image, useOtsu: false, threshold: 20, detectDark: false));
            Assert.Equal(expectedDark, BlobAlgorithm.Segment(image, useOtsu: false, threshold: 20, detectDark: true));
        }

        [Fact]
        public void Segment_Otsu_LightTarget_IsExclusiveAtThreshold()
        {
            // Two levels: five pixels at 20 and five at 240. Otsu picks the boundary 20.
            IntensityImage image = IntensityImageFactory.CreateGray8(10, 1, (x, _) => x < 5 ? (byte)20 : (byte)240);

            bool[] otsu = BlobAlgorithm.Segment(image, useOtsu: true, threshold: 0, detectDark: false);
            bool[] manual = BlobAlgorithm.Segment(image, useOtsu: false, threshold: 20, detectDark: false);

            // Otsu selects g > T, so the level that equals T stays background.
            Assert.Equal(5, CountForeground(otsu));
            // A manual light threshold is inclusive (g >= T), so both levels qualify.
            Assert.Equal(10, CountForeground(manual));
        }

        [Fact]
        public void Segment_Gray16_ManualThresholdScalesTo16BitRange()
        {
            IntensityImage image = IntensityImageFactory.CreateGray16(
                3, 1, (x, _) => x switch { 0 => (ushort)25699, 1 => (ushort)25700, _ => (ushort)65535 });
            bool[] expected = [false, true, true];

            // 100 * 257 = 25700, and the light threshold includes the boundary value.
            Assert.Equal(expected, BlobAlgorithm.Segment(image, useOtsu: false, threshold: 100, detectDark: false));
        }

        [Fact]
        public void Connect_EightConnectivity_JoinsDiagonalAndSplitsDistantPixels()
        {
            IReadOnlyList<BlobFeature> joined = BlobAlgorithm.Connect(BuildMask(5, 5, (0, 0), (1, 1)), 5, 5, minArea: 1);
            BlobFeature region = Assert.Single(joined);
            Assert.Equal(2, region.Area);

            IReadOnlyList<BlobFeature> apart = BlobAlgorithm.Connect(BuildMask(5, 5, (0, 0), (2, 2)), 5, 5, minArea: 1);
            Assert.Equal(2, apart.Count);
        }

        [Fact]
        public void Connect_MinArea_DropsSmallComponents()
        {
            // A 2x2 block (area 4) plus a two-pixel pair (area 2).
            bool[] mask = BuildMask(6, 5, (0, 0), (1, 0), (0, 1), (1, 1), (4, 4), (5, 4));

            BlobFeature region = Assert.Single(BlobAlgorithm.Connect(mask, 6, 5, minArea: 3));
            Assert.Equal(4, region.Area);
        }

        [Fact]
        public void Connect_OrdersRegionsRowMajorByFirstPixel()
        {
            IReadOnlyList<BlobFeature> regions = BlobAlgorithm.Connect(BuildMask(6, 5, (2, 0), (0, 3)), 6, 5, minArea: 1);

            Assert.Equal(2, regions.Count);
            Assert.Equal(0, regions[0].BoundingBox.Y);
            Assert.Equal(3, regions[1].BoundingBox.Y);
        }

        [Fact]
        public void Connect_ComputesAreaCentroidAndBoundingBox()
        {
            bool[] mask = BuildMask(5, 4, (1, 1), (2, 1), (3, 1), (1, 2), (2, 2), (3, 2));

            BlobFeature region = Assert.Single(BlobAlgorithm.Connect(mask, 5, 4, minArea: 1));

            Assert.Equal(6, region.Area);
            Assert.Equal(2.0, region.Centroid.X, 6);
            Assert.Equal(1.5, region.Centroid.Y, 6);
            Assert.Equal(1.0, region.BoundingBox.X);
            Assert.Equal(1.0, region.BoundingBox.Y);
            Assert.Equal(3.0, region.BoundingBox.Width);
            Assert.Equal(2.0, region.BoundingBox.Height);
        }

        [Fact]
        public void Segment_InsideMask_MarksMaskedOutPixelsAsBackground()
        {
            // Every pixel would pass the manual threshold without a mask.
            IntensityImage image = IntensityImageFactory.CreateGray8(3, 1, (_, _) => (byte)200);
            bool[] inside = [true, false, true];
            bool[] expected = [true, false, true];

            bool[] mask = BlobAlgorithm.Segment(image, inside, useOtsu: false, threshold: 100, detectDark: false);

            Assert.Equal(expected, mask);
        }

        [Fact]
        public void Segment_InsideMask_OtsuHistogramIgnoresMaskedOutPixels()
        {
            // ROI pixels are 50 and 60; two saturated pixels sit outside the mask. A whole-plane
            // Otsu would split at 60 and drop the 60 pixel, while the ROI-only histogram splits at 50.
            IntensityImage image = IntensityImageFactory.CreateGray8(
                4, 1, (x, _) => x switch { 0 => (byte)50, 1 => (byte)60, 2 => (byte)0, _ => (byte)255 });
            IntensityImage roiOnly = IntensityImageFactory.CreateGray8(
                2, 1, (x, _) => x == 0 ? (byte)50 : (byte)60);
            bool[] inside = [true, true, false, false];

            bool[] masked = BlobAlgorithm.Segment(image, inside, useOtsu: true, threshold: 0, detectDark: false);
            bool[] unmasked = BlobAlgorithm.Segment(image, useOtsu: true, threshold: 0, detectDark: false);
            bool[] reference = BlobAlgorithm.Segment(roiOnly, useOtsu: true, threshold: 0, detectDark: false);
            bool[] expected = [reference[0], reference[1], false, false];

            Assert.Equal(expected, masked);
            Assert.True(masked[1]);
            Assert.False(unmasked[1]);
        }

        [Fact]
        public void ExtractRegions_InsideMask_ClipsComponentsAtTheRoiEdge()
        {
            // One bright run of five pixels; the mask keeps only the first three.
            IntensityImage image = IntensityImageFactory.CreateGray8(5, 1, (_, _) => (byte)200);
            bool[] inside = [true, true, true, false, false];

            BlobFeature region = Assert.Single(
                BlobAlgorithm.ExtractRegions(image, inside, useOtsu: false, threshold: 100, detectDark: false, minArea: 1));

            Assert.Equal(3, region.Area);
            Assert.Equal(0.0, region.BoundingBox.X);
            Assert.Equal(3.0, region.BoundingBox.Width);
        }

        [Fact]
        public void Segment_InsideMask_LengthMismatchThrows()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(3, 2, (_, _) => (byte)0);

            Assert.Throws<ArgumentException>(
                () => BlobAlgorithm.Segment(image, [true, true, true], useOtsu: false, threshold: 10, detectDark: false));
        }

        [Fact]
        public void Segment_NullInsideMask_MatchesTheUnmaskedOverload()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(4, 3, (x, y) => (byte)((x * 40) + (y * 10)));

            Assert.Equal(
                BlobAlgorithm.Segment(image, useOtsu: true, threshold: 0, detectDark: false),
                BlobAlgorithm.Segment(image, inside: null, useOtsu: true, threshold: 0, detectDark: false));
        }

        [Fact]
        public void Connect_IncludeContours_TracesTheComponentBoundary()
        {
            bool[] mask = BuildMask(
                6, 6, (1, 1), (2, 1), (3, 1), (1, 2), (2, 2), (3, 2), (1, 3), (2, 3), (3, 3));

            BlobFeature region = Assert.Single(
                BlobAlgorithm.Connect(mask, 6, 6, minArea: 1, includeContours: true));

            Assert.NotNull(region.Contour);
            Assert.Equal(4, region.Contour!.Length);
            Assert.Equal(9, Math.Abs(PolygonArea(region.Contour)), 6);
        }

        [Fact]
        public void Connect_IncludeContours_HandlesNonRectangularComponents()
        {
            // L shape: three pixels across the top row plus two down the left column.
            bool[] mask = BuildMask(4, 4, (0, 0), (1, 0), (2, 0), (0, 1), (0, 2));

            BlobFeature region = Assert.Single(
                BlobAlgorithm.Connect(mask, 4, 4, minArea: 1, includeContours: true));

            Assert.NotNull(region.Contour);
            Assert.Equal(6, region.Contour!.Length);
            Assert.Equal(5, Math.Abs(PolygonArea(region.Contour)), 6);
        }

        [Fact]
        public void Connect_IncludeContours_DecimatesBeyondTheVertexBudget()
        {
            // A 40x40 diamond has a long staircase boundary; the budget must cap it.
            var mask = new bool[40 * 40];
            for (int y = 0; y < 40; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    mask[(y * 40) + x] = Math.Abs(x - 20) + Math.Abs(y - 20) <= 15;
                }
            }

            BlobFeature region = Assert.Single(
                BlobAlgorithm.Connect(mask, 40, 40, minArea: 1, includeContours: true, maximumContourVertices: 8));

            Assert.NotNull(region.Contour);
            Assert.InRange(region.Contour!.Length, 3, 8);
        }

        [Fact]
        public void Connect_WithoutContours_LeavesContourNull()
        {
            BlobFeature region = Assert.Single(BlobAlgorithm.Connect(BuildMask(3, 3, (0, 0)), 3, 3, minArea: 1));

            Assert.Null(region.Contour);
        }

        private static double PolygonArea(PointD[] points)
        {
            double area = 0;
            for (int i = 0; i < points.Length; i++)
            {
                PointD next = points[(i + 1) % points.Length];
                area += (points[i].X * next.Y) - (next.X * points[i].Y);
            }

            return area / 2;
        }

        private static bool[] BuildMask(int width, int height, params (int X, int Y)[] points)
        {
            var mask = new bool[width * height];
            foreach ((int x, int y) in points)
            {
                mask[(y * width) + x] = true;
            }

            return mask;
        }

        private static int CountForeground(bool[] mask)
        {
            int count = 0;
            foreach (bool value in mask)
            {
                if (value)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
