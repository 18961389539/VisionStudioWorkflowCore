using System.Collections.ObjectModel;

namespace ImageViewer.Core.Measurements;

/// <summary>
/// Framework-neutral statistics for a set of positive measurement samples.
/// The filtering and ordering rules are kept in Core so UI and batch consumers
/// report the same values.
/// </summary>
public sealed class MeasurementStatistics
{
    private static readonly IReadOnlyList<double> EmptySamples = Array.Empty<double>();

    private MeasurementStatistics(IReadOnlyList<double> samples)
    {
        Samples = samples;
        if (samples.Count == 0)
        {
            return;
        }

        Minimum = samples[0];
        Maximum = samples[^1];
        Mean = samples.Average();
        Median = samples.Count % 2 == 0
            ? (samples[(samples.Count / 2) - 1] + samples[samples.Count / 2]) / 2
            : samples[samples.Count / 2];
        double variance = samples.Sum(value => Math.Pow(value - Mean, 2)) / samples.Count;
        StandardDeviation = Math.Sqrt(Math.Max(0, variance));
    }

    /// <summary>Gets the shared empty statistics instance.</summary>
    public static MeasurementStatistics Empty { get; } = new(EmptySamples);

    /// <summary>
    /// Filters non-finite and non-positive values, sorts the samples, and computes aggregate statistics.
    /// </summary>
    /// <param name="samples">Raw measurement samples.</param>
    /// <returns>A deterministic statistics object.</returns>
    public static MeasurementStatistics FromSamples(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        double[] values = samples
            .Where(value => double.IsFinite(value) && value > 0)
            .OrderBy(value => value)
            .ToArray();

        return values.Length == 0
            ? Empty
            : new MeasurementStatistics(new ReadOnlyCollection<double>(values));
    }

    /// <summary>Gets the sorted, sanitized samples.</summary>
    public IReadOnlyList<double> Samples { get; }

    /// <summary>Gets the number of valid samples.</summary>
    public int SampleCount => Samples.Count;

    /// <summary>Gets a value indicating whether at least one valid sample exists.</summary>
    public bool IsMeasured => SampleCount > 0;

    /// <summary>Gets the smallest valid sample, or zero when empty.</summary>
    public double Minimum { get; }

    /// <summary>Gets the largest valid sample, or zero when empty.</summary>
    public double Maximum { get; }

    /// <summary>Gets the maximum-minus-minimum range, or zero when empty.</summary>
    public double Range => IsMeasured ? Maximum - Minimum : 0;

    /// <summary>Gets the arithmetic mean, or zero when empty.</summary>
    public double Mean { get; }

    /// <summary>Gets the median, or zero when empty.</summary>
    public double Median { get; }

    /// <summary>Gets the population standard deviation, or zero when empty.</summary>
    public double StandardDeviation { get; }
}
