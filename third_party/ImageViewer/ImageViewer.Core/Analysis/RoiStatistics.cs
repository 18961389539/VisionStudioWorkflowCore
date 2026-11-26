#pragma warning disable CS1591

namespace ImageViewer.Models
{
    public sealed class RoiStatistics
    {
        public int PixelCount { get; init; }
        public double Mean { get; init; }
        public ushort Min { get; init; }
        public ushort Max { get; init; }
        public double StandardDeviation { get; init; }
    }
}

#pragma warning restore CS1591
