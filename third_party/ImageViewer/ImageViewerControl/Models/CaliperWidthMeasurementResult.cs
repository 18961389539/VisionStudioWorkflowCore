using System;
using System.Collections.Generic;
using ImageViewer.Core.Measurements;

namespace ImageViewer.Models
{
    /// <summary>
    /// 双边缘卡尺的宽度结果及逐卡尺统计。
    /// Chinese: 保留每个有效卡尺的边缘间距，避免只用中心线的单个距离掩盖局部变窄或锥度。
    /// English: Width result and per-caliper statistics for a dual-edge caliper.
    /// </summary>
    public sealed class CaliperWidthMeasurementResult
    {
        private readonly MeasurementStatistics _statistics;

        public static CaliperWidthMeasurementResult Empty { get; } = new(Array.Empty<double>(), 0);

        public CaliperWidthMeasurementResult(IEnumerable<double> samples, double centerDistance)
        {
            ArgumentNullException.ThrowIfNull(samples);
            _statistics = MeasurementStatistics.FromSamples(samples);
            CenterDistance = double.IsFinite(centerDistance) && centerDistance > 0 ? centerDistance : 0;
        }

        public IReadOnlyList<double> Samples => _statistics.Samples;

        public int SampleCount => _statistics.SampleCount;

        public bool IsMeasured => _statistics.IsMeasured;

        public double CenterDistance { get; }

        public double Minimum => _statistics.Minimum;

        public double Maximum => _statistics.Maximum;

        public double Range => _statistics.Range;

        public double Mean => _statistics.Mean;

        public double Median => _statistics.Median;

        public double StandardDeviation => _statistics.StandardDeviation;
    }
}
