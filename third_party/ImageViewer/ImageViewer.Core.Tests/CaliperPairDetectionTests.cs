using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Diagnostic coverage for the dual-edge (width) caliper: a bright band on a dark background must
    /// produce one edge pair, and the pair is only valid when it straddles the caliper center.
    /// </summary>
    public class CaliperPairDetectionTests
    {
        [Fact]
        public void TryMeasurePair_CenterInsideBrightBand_FindsBothEdges()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(
                800, 200, (x, _) => (byte)(x is >= 380 and <= 520 ? 240 : 60));

            // Center sits inside the band but not exactly at its middle.
            bool found = CaliperAlgorithm.TryMeasurePair(
                image,
                new PointD(490, 100),
                new VectorD(1, 0),
                searchRange: 374,
                averagingHalfWidth: 3,
                sigma: 1.0,
                minimumGradient: 8,
                CaliperEdgePolarity.Any,
                minimumEdgeGap: 0,
                nominalEdgeGap: 0,
                nominalEdgeGapTolerance: 0,
                out CaliperEdgePair pair);

            Assert.True(found);
            Assert.InRange(pair.Edge1.Point.X, 379, 381);
            Assert.InRange(pair.Edge2.Point.X, 520, 522);
        }

        [Fact]
        public void TryMeasurePair_CenterOutsideBrightBand_FindsNoStraddlingPair()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(
                800, 200, (x, _) => (byte)(x is >= 380 and <= 520 ? 240 : 60));

            // Center in the dark background right of the band: both edges lie on the same side of the
            // center, and the pair rule requires the two edges to straddle it.
            bool found = CaliperAlgorithm.TryMeasurePair(
                image,
                new PointD(700, 100),
                new VectorD(1, 0),
                searchRange: 374,
                averagingHalfWidth: 3,
                sigma: 1.0,
                minimumGradient: 8,
                CaliperEdgePolarity.Any,
                minimumEdgeGap: 0,
                nominalEdgeGap: 0,
                nominalEdgeGapTolerance: 0,
                out _);

            Assert.False(found);
        }
    }
}
