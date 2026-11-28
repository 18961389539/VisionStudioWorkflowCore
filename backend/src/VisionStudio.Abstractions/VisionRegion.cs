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
    public const int MaxComponents = 10_000;
    public const int MaxRuns = 1_000_000;
    public const string ImagePixelCenterContract = "image-pixel-center-v1";
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<VisionRegionComponent> Components { get; }
    /// <summary>Coordinate convention carried with the region across graph/worker boundaries.</summary>
    public string CoordinateSystem { get; } = ImagePixelCenterContract;
    public int SchemaVersion { get; } = 1;
    /// <summary>Optional stable producer identity used by source-object mapping diagnostics.</summary>
    public string? SourceRegionId { get; }
    public VisionRegion(int width, int height, IReadOnlyList<VisionRegionComponent> components,
        string coordinateSystem = ImagePixelCenterContract, int schemaVersion = 1, string? sourceRegionId = null)
    {
        if (width is < 1 or > 32768 || height is < 1 or > 32768) throw new ArgumentException("Invalid region image dimensions.");
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count > MaxComponents) throw new ArgumentException("Region component budget exceeded.");
        if (string.IsNullOrWhiteSpace(coordinateSystem)) throw new ArgumentException("Region coordinate system is required.");
        if (schemaVersion != 1) throw new ArgumentException("Unsupported region schema version.");
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
        CoordinateSystem = coordinateSystem;
        SchemaVersion = schemaVersion;
        SourceRegionId = sourceRegionId;
    }
    public VisionRegion WithMetadata(string? coordinateSystem = null, int? schemaVersion = null, string? sourceRegionId = null)
        => new(Width, Height, Components, coordinateSystem ?? CoordinateSystem, schemaVersion ?? SchemaVersion, sourceRegionId ?? SourceRegionId);
    public override string ToString() => $"Region {Width}x{Height}, {Components.Count} objects";
}

public interface IVisionDomainImage : IVisionImage
{
    VisionRegion Domain { get; }
}
