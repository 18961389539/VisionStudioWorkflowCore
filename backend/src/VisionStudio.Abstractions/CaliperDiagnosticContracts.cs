namespace VisionStudio.Abstractions;

// 卡尺诊断数据结构（2026-11-25 从 VisionStudio.Engine 下沉）。
//
// 为什么放在契约程序集：这些记录被算法层（VisionStudio.Algorithms）、引擎运行时
// （VisionNodeRuntime）、宿主 API（诊断端点）三边共用。此前它们留在引擎根命名空间，
// 使算法层为了一个 DTO 必须反向引用整个引擎。
//
// 注意：捕获用的 AsyncLocal 作用域（CaliperCircleDiagnosticCapture /
// CaliperEdgeDiagnosticCapture）是**实现**，仍留在 VisionStudio.Engine。

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
