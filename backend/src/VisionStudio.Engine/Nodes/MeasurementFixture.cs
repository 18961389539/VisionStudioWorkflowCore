namespace VisionStudio.Engine.Nodes;

/// <summary>Optional reference-image to current-image rigid transform for measurement geometry.
/// Geometry parameters and geometry inputs are in reference coordinates; ROI inputs are already
/// in current image coordinates (for example roi.transform.roi) and must not be transformed twice.</summary>
internal static class MeasurementFixture
{
    public static VisionTransform2D? Resolve(NodeExecutionContext context)
    {
        if (!context.TryGetValue("transform", out var value)) return null;
        var transform = value.Require<VisionTransform2D>("transform");
        const double tolerance = 1e-6;
        if (transform.Matrix.Any(v => !double.IsFinite(v))
            || transform.SourceUnit != "px" || transform.TargetUnit != "px"
            || Math.Abs(transform.H20) > tolerance || Math.Abs(transform.H21) > tolerance || Math.Abs(transform.H22 - 1) > tolerance
            || Math.Abs(transform.H00 * transform.H00 + transform.H10 * transform.H10 - 1) > tolerance
            || Math.Abs(transform.H01 * transform.H01 + transform.H11 * transform.H11 - 1) > tolerance
            || Math.Abs(transform.H00 * transform.H01 + transform.H10 * transform.H11) > tolerance
            || Math.Abs(transform.H00 * transform.H11 - transform.H01 * transform.H10 - 1) > tolerance)
            throw new InvalidOperationException("Measurement transform must be a finite, orientation-preserving rigid px-to-px transform (translation and rotation only).");
        return transform;
    }

    public static VisionLine2D Apply(VisionLine2D line, VisionTransform2D? transform) => transform is null
        ? line : new VisionLine2D(transform.Apply(line.Start), transform.Apply(line.End));
}
