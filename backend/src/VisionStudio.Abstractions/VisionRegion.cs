namespace VisionStudio.Abstractions;

/// <summary>Inclusive pixel runs in image coordinates. No native handle crosses graph boundaries.</summary>
public readonly record struct VisionRegionRun(int Row, int StartColumn, int EndColumn);

public sealed class VisionRegionComponent
{
    public IReadOnlyList<VisionRegionRun> Runs { get; }
    public VisionRegionComponent(IReadOnlyList<VisionRegionRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count > VisionRegion.MaxRuns) throw new ArgumentException("Region run budget exceeded.");
        var copy = runs.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            var run = copy[i];
            if (run.Row < 0 || run.StartColumn < 0 || run.EndColumn < run.StartColumn)
                throw new ArgumentException("Invalid region run.");
            if (i > 0 && (run.Row < copy[i - 1].Row || run.Row == copy[i - 1].Row && run.StartColumn <= copy[i - 1].EndColumn))
                throw new ArgumentException("Region runs must be sorted and non-overlapping.");
        }
        Runs = Array.AsReadOnly(copy);
    }
}

public sealed class VisionRegion
{
    public const int MaxRuns = 1_000_000;
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<VisionRegionComponent> Components { get; }
    public VisionRegion(int width, int height, IReadOnlyList<VisionRegionComponent> components)
    {
        if (width is < 1 or > 32768 || height is < 1 or > 32768) throw new ArgumentException("Invalid region image dimensions.");
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count > 10000) throw new ArgumentException("Region component budget exceeded.");
        long runCount = 0;
        foreach (var component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            runCount += component.Runs.Count;
            if (runCount > MaxRuns) throw new ArgumentException("Region run budget exceeded.");
            if (component.Runs.Any(r => r.Row >= height || r.EndColumn >= width)) throw new ArgumentException("Region run lies outside image bounds.");
        }
        Width = width; Height = height;
        Components = Array.AsReadOnly(components.Where(c => c.Runs.Count > 0).ToArray());
    }
    public override string ToString() => $"Region {Width}x{Height}, {Components.Count} objects";
}

public interface IVisionDomainImage : IVisionImage
{
    VisionRegion Domain { get; }
}
