using System;
using System.Collections.Generic;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Direct unit tests for the managed caliper: subpixel edge location, polarity
    /// filtering, image-border truncation, selection clamping, and the radial direction
    /// used by circular calipers.
    /// </summary>
    public class CaliperAlgorithmTests
    {
        [Fact]
        public void MeasureEdges_HorizontalStep_DarkToLight_FindsSingleSubpixelEdge()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(20, 5, (x, _) => x < 10 ? (byte)0 : (byte)255);

            IReadOnlyList<CaliperEdge> edges = CaliperAlgorithm.MeasureEdges(
                image,
                center: new PointD(10, 2),
                measurementDirection: new VectorD(1, 0),
                searchRange: 6,
                averagingHalfWidth: 0,
                sigma: 1.0,
                minimumGradient: 10,
                CaliperEdgePolarity.DarkToLight);

            CaliperEdge edge = Assert.Single(edges);
            Assert.InRange(edge.Point.X, 9.0, 10.0);
            Assert.InRange(edge.Point.Y, 1.9, 2.1);
            Assert.True(edge.Score > 100, $"Edge amplitude {edge.Score:F1} should span the full step.");
        }

        [Fact]
        public void MeasureEdges_PolarityFilter_SelectsOnlyMatchingDirection()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(20, 5, (x, _) => x < 10 ? (byte)0 : (byte)255);

            Assert.Single(Measure(image, CaliperEdgePolarity.Any));
            Assert.Single(Measure(image, CaliperEdgePolarity.DarkToLight));
            Assert.Empty(Measure(image, CaliperEdgePolarity.LightToDark));
        }

        [Fact]
        public void MeasureEdges_UniformImageCrossingBorder_ProducesNoEdge()
        {
            // Reading out-of-image samples as 0 would fabricate a step at the border.
            // The caliper truncates the profile instead, so a uniform image stays edge-free.
            IntensityImage image = IntensityImageFactory.CreateGray8(20, 10, (_, _) => 128);

            IReadOnlyList<CaliperEdge> edges = CaliperAlgorithm.MeasureEdges(
                image,
                center: new PointD(2, 5),
                measurementDirection: new VectorD(1, 0),
                searchRange: 8,
                averagingHalfWidth: 0,
                sigma: 1.0,
                minimumGradient: 10,
                CaliperEdgePolarity.Any);

            Assert.Empty(edges);
        }

        [Fact]
        public void MeasureEdges_ProfileWithFewerThanThreeSamples_ReturnsEmpty()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(3, 3, (_, _) => 128);

            IReadOnlyList<CaliperEdge> edges = CaliperAlgorithm.MeasureEdges(
                image,
                center: new PointD(0, 1),
                measurementDirection: new VectorD(1, 0),
                searchRange: 1,
                averagingHalfWidth: 0,
                sigma: 1.0,
                minimumGradient: 10,
                CaliperEdgePolarity.Any);

            Assert.Empty(edges);
        }

        [Fact]
        public void TryMeasureSingle_SelectsEdgeByDescendingAmplitude()
        {
            // Three grey levels create two edges of different amplitude: a strong
            // dark-to-light step near x=5.5 and a weaker light-to-dark step near x=14.5.
            IntensityImage image = IntensityImageFactory.CreateGray8(
                24, 5, (x, _) => x < 6 ? (byte)0 : x < 15 ? (byte)255 : (byte)64);

            Assert.True(CaliperAlgorithm.TryMeasureSingle(
                image, new PointD(10, 2), new VectorD(1, 0), 9, 0, 1.0, 10, CaliperEdgePolarity.Any, 1, out CaliperEdge strongest));
            Assert.True(CaliperAlgorithm.TryMeasureSingle(
                image, new PointD(10, 2), new VectorD(1, 0), 9, 0, 1.0, 10, CaliperEdgePolarity.Any, 2, out CaliperEdge second));
            Assert.True(CaliperAlgorithm.TryMeasureSingle(
                image, new PointD(10, 2), new VectorD(1, 0), 9, 0, 1.0, 10, CaliperEdgePolarity.Any, 99, out CaliperEdge clamped));

            Assert.InRange(strongest.Point.X, 4.8, 6.2);
            Assert.InRange(second.Point.X, 13.8, 15.2);
            Assert.Equal(second.Point.X, clamped.Point.X, 6);
        }

        [Fact]
        public void TryMeasureCircular_RadialEdge_ConsistentAcrossQuadrants()
        {
            const double centerX = 20.0;
            const double centerY = 20.0;
            const double diskRadius = 8.0;
            IntensityImage image = IntensityImageFactory.CreateGray8(40, 40, (x, y) =>
            {
                double dx = x + 0.5 - centerX;
                double dy = y + 0.5 - centerY;
                return Math.Sqrt((dx * dx) + (dy * dy)) <= diskRadius ? (byte)255 : (byte)0;
            });

            foreach (int degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                double angle = degrees * Math.PI / 180.0;
                bool measured = CaliperAlgorithm.TryMeasureCircular(
                    image,
                    new PointD(centerX, centerY),
                    nominalRadius: diskRadius,
                    angleRadians: angle,
                    angleExtent: 0.1,
                    searchRange: 6,
                    averagingHalfWidth: 2,
                    sigma: 1.0,
                    minimumGradient: 10,
                    CaliperEdgePolarity.LightToDark,
                    edgeSelection: 1,
                    out CaliperEdge edge);

                Assert.True(measured, $"A radial edge should be found at {degrees} degrees.");
                double radius = Math.Sqrt(
                    ((edge.Point.X - centerX) * (edge.Point.X - centerX)) +
                    ((edge.Point.Y - centerY) * (edge.Point.Y - centerY)));
                Assert.InRange(radius, 6.5, 9.5);
            }
        }

        private static IReadOnlyList<CaliperEdge> Measure(IntensityImage image, CaliperEdgePolarity polarity)
        {
            return CaliperAlgorithm.MeasureEdges(
                image,
                center: new PointD(10, 2),
                measurementDirection: new VectorD(1, 0),
                searchRange: 6,
                averagingHalfWidth: 0,
                sigma: 1.0,
                minimumGradient: 10,
                polarity);
        }
    }
}
