using System.Text.Json;

// 2026-11-25 从 Engine/Nodes 迁入契约层：它是纯校验/诊断文本工具，
// 只依赖本程序集的坐标类型。算法层（ToleranceGeometry.Missing）与引擎共用它，
// 让它留在节点目录会迫使算法层反向依赖引擎。
namespace VisionStudio.Abstractions;

public static class CoordinateValidation
{
    public static double RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException($"{name} must be finite, got {value}.");
        return value;
    }
    public static VisionPoint2D RequireFinitePoint(VisionPoint2D point, string name)
    {
        RequireFinite(point.X, name + ".x"); RequireFinite(point.Y, name + ".y");
        return point;
    }
    public static VisionCoordinatePoint2D RequireFinitePoint(VisionCoordinatePoint2D point, string name)
    {
        RequireFinite(point.X, name + ".x"); RequireFinite(point.Y, name + ".y");
        RequireFrame(point.Frame, name + ".frame"); RequireUnit(point.Unit, name + ".unit");
        return point;
    }
    public static VisionCoordinatePose2D RequireFinitePose(VisionCoordinatePose2D pose, string name)
    {
        RequireFinite(pose.X, name + ".x"); RequireFinite(pose.Y, name + ".y"); RequireFinite(pose.ThetaDeg, name + ".thetaDeg");
        RequireFrame(pose.Frame, name + ".frame"); RequireUnit(pose.Unit, name + ".unit");
        return pose;
    }
    public static string RequireFrame(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} must name a coordinate frame (non-empty).");
        var trimmed = value.Trim();
        if (trimmed.Length > 64) throw new InvalidOperationException($"{name} frame name is too long (max 64 chars).");
        return trimmed;
    }
    public static string RequireUnit(string? value, string name, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} must name a unit (non-empty).");
        var trimmed = value.Trim();
        if (allowed.Length > 0 && !allowed.Any(u => u.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{name} unit '{trimmed}' is not supported here; allowed: {string.Join(", ", allowed)}.");
        return trimmed;
    }
    public static string RequireNonEmpty(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} must be non-empty.");
        return value.Trim();
    }
    public static double ValidateTransform(VisionTransform2D transform, string name)
    {
        RequireFrame(transform.SourceFrame, name + ".sourceFrame"); RequireFrame(transform.TargetFrame, name + ".targetFrame");
        RequireUnit(transform.SourceUnit, name + ".sourceUnit"); RequireUnit(transform.TargetUnit, name + ".targetUnit");
        foreach (var v in transform.Matrix) RequireFinite(v, name + ".matrix");
        var det = Determinant(transform);
        RequireFinite(det, name + ".determinant");
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException($"{name} transform is singular (|det| < 1e-12).");
        return det;
    }
    public static double Determinant(VisionTransform2D t)
        => t.H00 * (t.H11 * t.H22 - t.H12 * t.H21) - t.H01 * (t.H10 * t.H22 - t.H12 * t.H20) + t.H02 * (t.H10 * t.H21 - t.H11 * t.H20);
    public static double RequireLine(VisionLine2D line, string name)
    {
        RequireFinitePoint(line.Start, name + ".start"); RequireFinitePoint(line.End, name + ".end");
        var length = line.Length;
        if (!double.IsFinite(length) || length < 1e-9) throw new InvalidOperationException($"{name} must be a non-degenerate line (length >= 1e-9).");
        return length;
    }
    public static double RequireMagnitude(double value, string name, double limit)
    {
        RequireFinite(value, name);
        if (Math.Abs(value) > limit) throw new InvalidOperationException($"{name} magnitude {value} exceeds the allowed limit {limit}.");
        return value;
    }

    public static void RequireFrameUnitMatch(string frameA, string unitA, string frameB, string unitB, string operation)
    {
        if (!string.Equals(frameA, frameB, StringComparison.OrdinalIgnoreCase) || !string.Equals(unitA, unitB, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{operation} mismatch: A frame='{frameA}' unit='{unitA}'; B frame='{frameB}' unit='{unitB}' (frameMatch={string.Equals(frameA, frameB, StringComparison.OrdinalIgnoreCase)}, unitMatch={string.Equals(unitA, unitB, StringComparison.OrdinalIgnoreCase)}).");
    }

    public static double SingularityThreshold(VisionTransform2D transform)
    {
        var scale = Math.Max(1, Math.Max(Math.Abs(transform.H00), Math.Max(Math.Abs(transform.H01), Math.Max(Math.Abs(transform.H10), Math.Abs(transform.H11)))));
        return 1e-12 * scale * scale;
    }

    /// <summary>2-norm condition number of the affine linear block (translation-independent).</summary>
    public static double ConditionNumber(VisionTransform2D transform)
    {
        var a = Math.Max(Math.Abs(transform.H00), Math.Abs(transform.H01));
        var c = Math.Max(Math.Abs(transform.H10), Math.Abs(transform.H11));
        var norm = Math.Max(a + c, 1e-300);
        var determinant = Determinant(transform);
        if (Math.Abs(determinant) < 1e-300) return double.PositiveInfinity;
        // ||A^-1||_1 for a 2x2 matrix; infinity and 1-norm condition numbers are equivalent for 2x2.
        var inverseNorm = Math.Max(Math.Abs(transform.H11) + Math.Abs(transform.H10), Math.Abs(transform.H01) + Math.Abs(transform.H00)) / Math.Abs(determinant);
        return norm * inverseNorm;
    }

    public static string MatrixType(VisionTransform2D transform)
    {
        var rigid = Math.Abs(transform.H00 * transform.H00 + transform.H10 * transform.H10 - 1) < 1e-9
            && Math.Abs(transform.H01 * transform.H01 + transform.H11 * transform.H11 - 1) < 1e-9
            && Math.Abs(transform.H00 * transform.H01 + transform.H10 * transform.H11) < 1e-9;
        return rigid ? "rigid" : "affine-nonrigid";
    }

    public static string Diagnostics(string nodeType, params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?> { ["node"] = nodeType, ["valid"] = true };
        foreach (var (key, value) in fields)
            payload[key] = value is double d ? (double.IsFinite(d) ? Math.Round(d, 6) : d.ToString()) : value;
        return JsonSerializer.Serialize(payload);
    }
}
