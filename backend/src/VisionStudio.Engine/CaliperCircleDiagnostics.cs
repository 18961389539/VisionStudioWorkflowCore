namespace VisionStudio.Engine;

/// <summary>Interactive run artifacts. Profiles never enter reports or production trace storage.</summary>
public sealed record CaliperEdgeCandidate(double Position, double Amplitude, string Method);

public sealed class CaliperSpokeDiagnostic(int index, double angleDeg, VisionPoint2D start, VisionPoint2D end)
{
    public int Index { get; } = index;
    public double AngleDeg { get; } = angleDeg;
    public VisionPoint2D Start { get; } = start;
    public VisionPoint2D End { get; } = end;
    public VisionPoint2D? ProfileStart { get; set; }
    public VisionPoint2D? ProfileEnd { get; set; }
    public bool Clipped { get; set; }
    public string Status { get; set; } = "outside-image";
    public double StartRadius { get; set; }
    public double Spacing { get; set; }
    public double[] Profile { get; set; } = [];
    public List<CaliperEdgeCandidate> Candidates { get; } = [];
    public double? EdgeRadius { get; set; }
    public double? EdgeStrength { get; set; }
    public VisionPoint2D? EdgePoint { get; set; }

    public long EstimatedBytes => 512L + Profile.LongLength * sizeof(double) + Candidates.Count * 64L;
}

public sealed record CaliperCircleDiagnostic(
    int ImageWidth, int ImageHeight, string? CoordinateFrame,
    VisionPoint2D Center, double NominalRadius, double RadialBand,
    double MinContrast, int SampleWidth, string Polarity, string SearchDirection,
    bool Found, string Reason, IReadOnlyList<CaliperSpokeDiagnostic> Spokes)
{
    public long EstimatedBytes => 1024L + Spokes.Sum(spoke => spoke.EstimatedBytes);
}

public sealed record RunCaliperCircleDiagnostic(string NodeId, long ExecutionSequence, CaliperCircleDiagnostic Data);

/// <summary>
/// A scope belongs to one node dispatch, including on parallel branches. This keeps diagnostic
/// capture out of the plugin execution ABI and avoids retaining any profiles in production runs.
/// </summary>
internal static class CaliperCircleDiagnosticCapture
{
    private static readonly AsyncLocal<Action<CaliperCircleDiagnostic>?> Current = new();
    public static bool Enabled => Current.Value is not null;
    public static void Publish(CaliperCircleDiagnostic diagnostic) => Current.Value?.Invoke(diagnostic);

    public static IDisposable Begin(Action<CaliperCircleDiagnostic> capture)
    {
        var previous = Current.Value;
        Current.Value = capture;
        return new Scope(previous);
    }

    private sealed class Scope(Action<CaliperCircleDiagnostic>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
