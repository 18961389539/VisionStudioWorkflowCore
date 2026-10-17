namespace VisionStudio.Engine;

/// <summary>Actual single-caliper samples retained only for interactive runs.</summary>
public sealed record CaliperEdgeDiagnostic(
    int ImageWidth, int ImageHeight, string? CoordinateFrame,
    VisionPoint2D Start, VisionPoint2D End, VisionPoint2D? ProfileStart, VisionPoint2D? ProfileEnd,
    bool Clipped, double StartOffset, double Spacing, double MinContrast, int SampleWidth,
    string Polarity, string Direction, string Refine, bool Found, string Reason,
    double[] Profile, IReadOnlyList<CaliperEdgeCandidate> Candidates, int? SelectedCandidateIndex,
    double? EdgePosition, VisionPoint2D? EdgePoint, double? EdgeStrength, double? FitError)
{
    public long EstimatedBytes => 1024L + Profile.LongLength * sizeof(double) + Candidates.Count * 64L;
}

public sealed record RunCaliperEdgeDiagnostic(string NodeId, long ExecutionSequence, CaliperEdgeDiagnostic Data);

internal static class CaliperEdgeDiagnosticCapture
{
    private static readonly AsyncLocal<Action<CaliperEdgeDiagnostic>?> Current = new();
    public static bool Enabled => Current.Value is not null;
    public static void Publish(CaliperEdgeDiagnostic diagnostic) => Current.Value?.Invoke(diagnostic);

    public static IDisposable Begin(Action<CaliperEdgeDiagnostic> capture)
    {
        var previous = Current.Value;
        Current.Value = capture;
        return new Scope(previous);
    }

    private sealed class Scope(Action<CaliperEdgeDiagnostic>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
