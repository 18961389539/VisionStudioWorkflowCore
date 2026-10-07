namespace VisionStudio.Abstractions;

public static class VisionTransform2DMath
{
    public static VisionTransform2D Rigid(
        string sourceFrame,
        string targetFrame,
        string unit,
        double x,
        double y,
        double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        return new VisionTransform2D(
            sourceFrame, targetFrame, unit,
            c, -s, x,
            s, c, y,
            0, 0, 1,
            unit);
    }

    /// <summary>A maps S->M, B maps M->T. Result maps S->T (B * A).</summary>
    public static VisionTransform2D Compose(VisionTransform2D a, VisionTransform2D b)
    {
        RequireFrameUnitMatch(a.TargetFrame, a.TargetUnit, b.SourceFrame, b.SourceUnit, "compose");
        var am = ToMatrix(a);
        var bm = ToMatrix(b);
        var m = Multiply(bm, am);
        return FromMatrix(a.SourceFrame, b.TargetFrame, a.SourceUnit, b.TargetUnit, m);
    }

    public static VisionTransform2D Inverse(VisionTransform2D t)
    {
        var inv = Invert3x3(ToMatrix(t));
        return FromMatrix(t.TargetFrame, t.SourceFrame, t.TargetUnit, t.SourceUnit, inv);
    }

    public static VisionCoordinatePoint2D Apply(VisionTransform2D transform, VisionCoordinatePoint2D point)
    {
        RequireFrameUnitMatch(point.Frame, point.Unit, transform.SourceFrame, transform.SourceUnit, "point transform");
        var q = transform.Apply(new VisionPoint2D(point.X, point.Y));
        return new VisionCoordinatePoint2D(q.X, q.Y, transform.TargetFrame, transform.TargetUnit);
    }

    public static VisionCoordinatePose2D Apply(VisionTransform2D transform, VisionCoordinatePose2D pose)
    {
        RequireFrameUnitMatch(pose.Frame, pose.Unit, transform.SourceFrame, transform.SourceUnit, "pose transform");
        var origin = new VisionPoint2D(pose.X, pose.Y);
        var q = transform.Apply(origin);
        var angle = transform.ApplyAngleDeg(origin, pose.ThetaDeg);
        return new VisionCoordinatePose2D(q.X, q.Y, angle, transform.TargetFrame, transform.TargetUnit);
    }

    public static void RequireFrameUnitMatch(string frameA, string unitA, string frameB, string unitB, string operation)
    {
        if (!string.Equals(frameA, frameB, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Frame mismatch during {operation}: '{frameA}' != '{frameB}'.");
        if (!string.Equals(unitA, unitB, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unit mismatch during {operation}: '{unitA}' != '{unitB}'.");
    }

    private static double[,] ToMatrix(VisionTransform2D t) => new[,]
    {
        { t.H00, t.H01, t.H02 },
        { t.H10, t.H11, t.H12 },
        { t.H20, t.H21, t.H22 }
    };

    private static VisionTransform2D FromMatrix(string sourceFrame, string targetFrame, string sourceUnit, string targetUnit, double[,] m) =>
        new(sourceFrame, targetFrame, targetUnit,
            m[0,0], m[0,1], m[0,2],
            m[1,0], m[1,1], m[1,2],
            m[2,0], m[2,1], m[2,2],
            sourceUnit);

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3,3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                for (var k = 0; k < 3; k++)
                    r[i,j] += a[i,k] * b[k,j];
        return r;
    }

    private static double[,] Invert3x3(double[,] m)
    {
        var a=m[0,0]; var b=m[0,1]; var c=m[0,2];
        var d=m[1,0]; var e=m[1,1]; var f=m[1,2];
        var g=m[2,0]; var h=m[2,1]; var i=m[2,2];
        var det = a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g);
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Transform2D matrix is singular and cannot be inverted.");
        var invDet=1.0/det;
        return new[,]
        {
            { (e*i-f*h)*invDet, (c*h-b*i)*invDet, (b*f-c*e)*invDet },
            { (f*g-d*i)*invDet, (a*i-c*g)*invDet, (c*d-a*f)*invDet },
            { (d*h-e*g)*invDet, (b*g-a*h)*invDet, (a*e-b*d)*invDet }
        };
    }
}

public sealed record VisionFrameResolveResult(VisionTransform2D Transform, IReadOnlyList<string> Path);

public sealed class VisionFrameTree2D
{
    public VisionFrameTree2D(IEnumerable<VisionTransform2D> transforms)
    {
        Transforms = transforms.ToArray();
        if (Transforms.Count == 0) throw new InvalidOperationException("FrameTree2D requires at least one transform.");
    }

    public IReadOnlyList<VisionTransform2D> Transforms { get; }

    public VisionFrameResolveResult Resolve(string sourceFrame, string targetFrame)
    {
        if (sourceFrame.Equals(targetFrame, StringComparison.OrdinalIgnoreCase))
        {
            var unit = Transforms.FirstOrDefault(x => x.SourceFrame.Equals(sourceFrame, StringComparison.OrdinalIgnoreCase))?.SourceUnit
                ?? Transforms.FirstOrDefault(x => x.TargetFrame.Equals(sourceFrame, StringComparison.OrdinalIgnoreCase))?.TargetUnit
                ?? "mm";
            return new VisionFrameResolveResult(VisionTransform2DMath.Rigid(sourceFrame, targetFrame, unit, 0, 0, 0), [sourceFrame]);
        }

        var edges = new List<VisionTransform2D>();
        foreach (var t in Transforms)
        {
            edges.Add(t);
            edges.Add(VisionTransform2DMath.Inverse(t));
        }

        var queue = new Queue<(string Frame, VisionTransform2D? Acc, List<string> Path)>();
        queue.Enqueue((sourceFrame, null, [sourceFrame]));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceFrame };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges.Where(x => x.SourceFrame.Equals(current.Frame, StringComparison.OrdinalIgnoreCase)))
            {
                if (!visited.Add(edge.TargetFrame)) continue;
                var acc = current.Acc is null ? edge : VisionTransform2DMath.Compose(current.Acc, edge);
                var path = new List<string>(current.Path) { edge.TargetFrame };
                if (edge.TargetFrame.Equals(targetFrame, StringComparison.OrdinalIgnoreCase))
                    return new VisionFrameResolveResult(acc, path);
                queue.Enqueue((edge.TargetFrame, acc, path));
            }
        }

        throw new InvalidOperationException($"No transform path exists from '{sourceFrame}' to '{targetFrame}'.");
    }
}
