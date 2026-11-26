using ImageViewer.Core.Analysis;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// Bilinear sampling keeps caliper profiles smooth at subpixel positions while preserving
    /// the full Gray16 sample range and the nearest-neighbor border behaviour.
    /// </summary>
    public class IntensityImageSamplingTests
    {
        [Fact]
        public void SampleBilinear_IntegerCoordinates_MatchSampleNearest()
        {
            IntensityImage image = IntensityImageFactory.CreateGray16(2, 2, (x, y) => (ushort)(((y * 2) + x) * 1000));

            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    Assert.Equal(image.SampleNearest(x, y), image.SampleBilinear(x, y), 9);
                }
            }
        }

        [Fact]
        public void SampleBilinear_MidpointAveragesFourNeighbours()
        {
            IntensityImage image = IntensityImageFactory.CreateGray16(
                2,
                2,
                (x, y) => (ushort)(((y * 2) + x) switch { 0 => 0, 1 => 10000, 2 => 30000, _ => 40000 }));

            Assert.Equal(20000.0, image.SampleBilinear(0.5, 0.5), 6);
            Assert.Equal(5000.0, image.SampleBilinear(0.5, 0), 6);
            Assert.Equal(35000.0, image.SampleBilinear(0.5, 1), 6);
        }

        [Fact]
        public void SampleBilinear_OutsideImage_ClampsToBorder()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(3, 3, (x, _) => (byte)((x + 1) * 10));

            Assert.Equal(10.0, image.SampleBilinear(-5, 1), 9);
            Assert.Equal(30.0, image.SampleBilinear(100, 1), 9);
        }

        [Fact]
        public void SampleBilinear_NonFiniteCoordinate_ReturnsZero()
        {
            IntensityImage image = IntensityImageFactory.CreateGray8(2, 2, (_, _) => 255);

            Assert.Equal(0.0, image.SampleBilinear(double.NaN, 0));
            Assert.Equal(0.0, image.SampleBilinear(0, double.PositiveInfinity));
        }
    }
}
