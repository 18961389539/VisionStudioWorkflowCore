using System.Text.Json;
using VisionStudio.Engine.Runtime;
namespace VisionStudio.Engine.Nodes;
public sealed record CalibrationPair(double ImageX, double ImageY, double WorldX, double WorldY);
public static class CalibrationMath
{
    private sealed record NormalizedPoints(double[,] Values, double Scale, double MeanX, double MeanY, double NormalizationScale);

    public static VisionTransform2D SolveHomography(IReadOnlyList<CalibrationPair> pairs, string sourceFrame, string targetFrame, string unit, CancellationToken cancellationToken = default)
    {
        if (pairs.Count < 4) throw new InvalidOperationException("Planar calibration requires at least 4 point pairs.");
        foreach (var p in pairs) if (!double.IsFinite(p.ImageX) || !double.IsFinite(p.ImageY) || !double.IsFinite(p.WorldX) || !double.IsFinite(p.WorldY)) throw new InvalidOperationException("Planar calibration pairs must all be finite.");
        var image = Normalize(pairs, imageSpace: true);
        var world = Normalize(pairs, imageSpace: false);
        var rows = checked(pairs.Count * 2);
        var design = new double[rows, 8]; var right = new double[rows];
        for (var i = 0; i < pairs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = image.Values[i, 0]; var y = image.Values[i, 1];
            var u = world.Values[i, 0]; var v = world.Values[i, 1]; var row = i * 2;
            design[row, 0] = x; design[row, 1] = y; design[row, 2] = 1;
            design[row, 6] = -u * x; design[row, 7] = -u * y; right[row] = u;
            design[row + 1, 3] = x; design[row + 1, 4] = y; design[row + 1, 5] = 1;
            design[row + 1, 6] = -v * x; design[row + 1, 7] = -v * y; right[row + 1] = v;
        }

        var normalized = SolveLeastSquaresQr(design, right, cancellationToken);
        var normalizedHomography = new double[3, 3]
        {
            { normalized[0], normalized[1], normalized[2] },
            { normalized[3], normalized[4], normalized[5] },
            { normalized[6], normalized[7], 1d }
        };
        var sourceNormalization = new double[3, 3]
        {
            { image.NormalizationScale / image.Scale, 0, -image.MeanX * image.NormalizationScale },
            { 0, image.NormalizationScale / image.Scale, -image.MeanY * image.NormalizationScale },
            { 0, 0, 1 }
        };
        var targetDenormalization = new double[3, 3]
        {
            { world.Scale / world.NormalizationScale, 0, world.Scale * world.MeanX },
            { 0, world.Scale / world.NormalizationScale, world.Scale * world.MeanY },
            { 0, 0, 1 }
        };
        var homography = Multiply(Multiply(targetDenormalization, normalizedHomography), sourceNormalization);
        var scale = homography[2, 2];
        if (!double.IsFinite(scale) || Math.Abs(scale) < 1e-14)
            throw new InvalidOperationException("Planar calibration produced a homography that cannot be represented with a finite H22=1 scale.");
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++) homography[row, column] /= scale;
        if (homography.Cast<double>().Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("Planar calibration produced non-finite transform coefficients.");
        return new VisionTransform2D(sourceFrame, targetFrame, unit,
            homography[0, 0], homography[0, 1], homography[0, 2],
            homography[1, 0], homography[1, 1], homography[1, 2],
            homography[2, 0], homography[2, 1], homography[2, 2]);
    }

    private static NormalizedPoints Normalize(IReadOnlyList<CalibrationPair> pairs, bool imageSpace)
    {
        double X(CalibrationPair point) => imageSpace ? point.ImageX : point.WorldX;
        double Y(CalibrationPair point) => imageSpace ? point.ImageY : point.WorldY;
        var scale = pairs.Max(point => Math.Max(Math.Abs(X(point)), Math.Abs(Y(point))));
        if (scale == 0) scale = 1;
        var meanX = pairs.Average(point => X(point) / scale); var meanY = pairs.Average(point => Y(point) / scale);
        var values = new double[pairs.Count, 2]; double meanDistance = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var x = X(pairs[i]) / scale - meanX; var y = Y(pairs[i]) / scale - meanY;
            values[i, 0] = x; values[i, 1] = y;
            meanDistance += Hypot(x, y) / pairs.Count;
        }
        if (!double.IsFinite(meanDistance) || meanDistance < 1e-15)
            throw new InvalidOperationException($"Planar calibration {(imageSpace ? "image" : "world")} points have insufficient spatial spread.");
        var normalizationScale = Math.Sqrt(2) / meanDistance;
        for (var i = 0; i < pairs.Count; i++) { values[i, 0] *= normalizationScale; values[i, 1] *= normalizationScale; }
        return new(values, scale, meanX, meanY, normalizationScale);
    }

    private static double[] SolveLeastSquaresQr(double[,] a, double[] b, CancellationToken cancellationToken)
    {
        var rows = a.GetLength(0); const int columns = 8;
        var permutation = Enumerable.Range(0, columns).ToArray(); var firstDiagonal = 0d;
        for (var k = 0; k < columns; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pivotColumn = k; var largestNormSquared = -1d;
            for (var column = k; column < columns; column++)
            {
                double normSquared = 0;
                for (var row = k; row < rows; row++) normSquared += a[row, column] * a[row, column];
                if (normSquared > largestNormSquared) { largestNormSquared = normSquared; pivotColumn = column; }
            }
            if (pivotColumn != k)
            {
                for (var row = 0; row < rows; row++) (a[row, k], a[row, pivotColumn]) = (a[row, pivotColumn], a[row, k]);
                (permutation[k], permutation[pivotColumn]) = (permutation[pivotColumn], permutation[k]);
            }
            var norm = Math.Sqrt(Math.Max(0d, largestNormSquared));
            if (k == 0) firstDiagonal = norm;
            if (!double.IsFinite(norm) || norm <= Math.Max(1e-12, firstDiagonal * 1e-12))
                throw new InvalidOperationException("Calibration point geometry is rank-deficient or poorly conditioned after coordinate normalization.");
            var alpha = a[k, k] >= 0 ? -norm : norm;
            var vector = new double[rows - k]; vector[0] = a[k, k] - alpha;
            for (var row = k + 1; row < rows; row++) vector[row - k] = a[row, k];
            double vectorNormSquared = 0; foreach (var value in vector) vectorNormSquared += value * value;
            if (!double.IsFinite(vectorNormSquared) || vectorNormSquared <= 1e-30)
                throw new InvalidOperationException("Calibration point geometry is rank-deficient or poorly conditioned after coordinate normalization.");
            var beta = 2d / vectorNormSquared;
            for (var column = k + 1; column < columns; column++)
            {
                double dot = 0; for (var row = k; row < rows; row++) dot += vector[row - k] * a[row, column];
                var factor = beta * dot; for (var row = k; row < rows; row++) a[row, column] -= factor * vector[row - k];
            }
            double rhsDot = 0; for (var row = k; row < rows; row++) rhsDot += vector[row - k] * b[row];
            var rhsFactor = beta * rhsDot; for (var row = k; row < rows; row++) b[row] -= rhsFactor * vector[row - k];
            a[k, k] = alpha; for (var row = k + 1; row < rows; row++) a[row, k] = 0;
        }
        var pivotedSolution = new double[columns];
        for (var row = columns - 1; row >= 0; row--)
        {
            var value = b[row]; for (var column = row + 1; column < columns; column++) value -= a[row, column] * pivotedSolution[column];
            pivotedSolution[row] = value / a[row, row];
        }
        var result = new double[columns]; for (var i = 0; i < columns; i++) result[permutation[i]] = pivotedSolution[i];
        return result;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
            for (var k = 0; k < 3; k++) result[row, column] += a[row, k] * b[k, column];
        return result;
    }

    private static double Hypot(double x, double y) { var max = Math.Max(Math.Abs(x), Math.Abs(y)); return max == 0 ? 0 : max * Math.Sqrt((x / max) * (x / max) + (y / max) * (y / max)); }

    public static double Distance(double dx, double dy) => Hypot(dx, dy);

    public static double RootMeanSquare(IEnumerable<double> values)
    {
        var array = values.ToArray();
        if (array.Length == 0) return 0;
        var scale = array.Max(value => Math.Abs(value));
        return scale == 0 ? 0 : scale * Math.Sqrt(array.Average(value => (value / scale) * (value / scale)));
    }
}
public sealed class PlanarCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.planar";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CalibrationPair> pairs;
        try { pairs=JsonSerializer.Deserialize<List<CalibrationPair>>(node.GetString("pairsJson","[]"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"calibration.planar pairsJson is not valid JSON: {ex.Message}"); }
        if (pairs.Count < 4) throw new InvalidOperationException("calibration.planar requires at least 4 point pairs in pairsJson.");
        if (pairs.Count > 10000) throw new InvalidOperationException("calibration.planar accepts at most 10000 point pairs.");
        var source=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","ImagePixel"),"sourceFrame");
        var target=CoordinateValidation.RequireFrame(node.GetString("targetFrame","Workpiece"),"targetFrame");
        var unit=CoordinateValidation.RequireUnit(node.GetString("targetUnit","mm"),"targetUnit","mm","cm","m","px");
        var assetVersion=node.GetInt("assetVersion",0);
        if (assetVersion < 0) throw new InvalidOperationException("calibration.planar assetVersion must be >= 0.");
        var transform=CalibrationMath.SolveHomography(pairs,source,target,unit,cancellationToken);
        var det=CoordinateValidation.ValidateTransform(transform,"calibration.planar result");
        var residuals=pairs.Select((p,index)=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predicted=transform.Apply(new VisionPoint2D(p.ImageX,p.ImageY));
            var dx=predicted.X-p.WorldX; var dy=predicted.Y-p.WorldY;
            return new { pairIndex=index, imageX=p.ImageX, imageY=p.ImageY, worldX=p.WorldX, worldY=p.WorldY,
                predictedX=predicted.X, predictedY=predicted.Y, dx, dy, error=CalibrationMath.Distance(dx,dy) };
        }).ToArray();
        var errors=residuals.Select(residual=>residual.error).ToArray();
        if (errors.Any(e=>!double.IsFinite(e))) throw new InvalidOperationException("calibration.planar produced non-finite residuals.");
        var rmse=CalibrationMath.RootMeanSquare(errors); var max=errors.Max(); var worst=Array.IndexOf(errors,max);
        var maxRmse=CoordinateValidation.RequireFinite(node.GetDouble("maxRmse",0),"maxRmse");
        var maxPointError=CoordinateValidation.RequireFinite(node.GetDouble("maxPointError",0),"maxPointError");
        if (maxRmse < 0 || maxPointError < 0) throw new InvalidOperationException("calibration.planar maxRmse and maxPointError must be >= 0 (0 disables the gate).");
        if (maxRmse > 0 && rmse > maxRmse) throw new InvalidOperationException($"calibration.planar RMSE {rmse:F6} {unit} exceeds maxRmse {maxRmse:F6} {unit}.");
        if (maxPointError > 0 && max > maxPointError) throw new InvalidOperationException($"calibration.planar pair {worst} error {max:F6} {unit} exceeds maxPointError {maxPointError:F6} {unit}.");
        var residualsJson=JsonSerializer.Serialize(residuals);
        var diag=CoordinateValidation.Diagnostics(Type,("pointCount",pairs.Count),("rmse",rmse),("maxError",max),("worstPairIndex",worst),("maxRmse",maxRmse),("maxPointError",maxPointError),("determinant",det),("sourceFrame",source),("targetFrame",target),("unit",unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"rmse",VisionValue.Double(rmse)},{"maxError",VisionValue.Double(max)},{"pointCount",VisionValue.Integer(pairs.Count)},{"worstPairIndex",VisionValue.Integer(worst)},{"residualsJson",VisionValue.String(residualsJson)},{"diagnostics",VisionValue.String(diag)}},
            new Dictionary<string,object?>{{"points",pairs.Count},{"sourceFrame",source},{"targetFrame",target},{"unit",unit},{"assetId",string.IsNullOrWhiteSpace(node.GetString("assetId",""))?null:node.GetString("assetId","")},{"assetVersion",assetVersion>0?assetVersion:null},{"rmse",Math.Round(rmse,6)},{"maxError",Math.Round(max,6)},{"worstPairIndex",worst},{"residualsJson",residualsJson},{"diagnostics",diag}}));
    }
}
public sealed class TransformPointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformPoint";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p=CoordinateValidation.RequireFinitePoint(context.Require<VisionPoint2D>("point"),"point");
        var t=context.Require<VisionTransform2D>("transform");
        var det=CoordinateValidation.ValidateTransform(t,"transform");
        var q=CoordinateValidation.RequireFinitePoint(t.Apply(p),"transformed point");
        var world=new VisionCoordinatePoint2D(q.X,q.Y,t.TargetFrame,t.TargetUnit);
        var diag=CoordinateValidation.Diagnostics(Type,("inputX",p.X),("inputY",p.Y),("outputX",q.X),("outputY",q.Y),("determinant",det),("targetFrame",t.TargetFrame),("unit",t.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(world)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)},{"frame",VisionValue.String(t.TargetFrame)},{"unit",VisionValue.String(t.TargetUnit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"point",$"({q.X:0.###},{q.Y:0.###}) {t.TargetUnit}"},{"frame",t.TargetFrame},{"diagnostics",diag}}));
    }
}
public sealed class TransformLineNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformLine";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line=context.Require<VisionLine2D>("line");
        var inputLength=CoordinateValidation.RequireLine(line,"line");
        var t=context.Require<VisionTransform2D>("transform");
        var det=CoordinateValidation.ValidateTransform(t,"transform");
        var a=CoordinateValidation.RequireFinitePoint(t.Apply(line.Start),"transformed line.start");
        var b=CoordinateValidation.RequireFinitePoint(t.Apply(line.End),"transformed line.end");
        var wl=new VisionCoordinateLine2D(new(a.X,a.Y,t.TargetFrame,t.TargetUnit),new(b.X,b.Y,t.TargetFrame,t.TargetUnit));
        if (!double.IsFinite(wl.Length)) throw new InvalidOperationException("coordinate.transformLine produced a non-finite length.");
        var angle=Math.Atan2(b.Y-a.Y,b.X-a.X)*180/Math.PI;
        CoordinateValidation.RequireFinite(angle,"transformed line.angleDeg");
        var diag=CoordinateValidation.Diagnostics(Type,("inputLength",inputLength),("outputLength",wl.Length),("angleDeg",angle),("determinant",det),("targetFrame",t.TargetFrame),("unit",t.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"line",VisionValue.CoordinateLine(wl)},{"length",VisionValue.Measurement(new(wl.Length,t.TargetUnit,"Distance"))},{"lengthValue",VisionValue.Double(wl.Length)},{"angleDeg",VisionValue.Double(angle)},{"frame",VisionValue.String(t.TargetFrame)},{"unit",VisionValue.String(t.TargetUnit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"length",Math.Round(wl.Length,4)},{"unit",t.TargetUnit},{"frame",t.TargetFrame},{"diagnostics",diag}}));
    }
}
public sealed class TransformPoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformPose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pose=context.Require<VisionPose2D>("pose");
        CoordinateValidation.RequireFinite(pose.X,"pose.x"); CoordinateValidation.RequireFinite(pose.Y,"pose.y"); CoordinateValidation.RequireFinite(pose.ThetaDeg,"pose.thetaDeg");
        var t=context.Require<VisionTransform2D>("transform");
        var det=CoordinateValidation.ValidateTransform(t,"transform");
        var q=CoordinateValidation.RequireFinitePoint(t.Apply(new VisionPoint2D(pose.X,pose.Y)),"transformed pose");
        var angle=CoordinateValidation.RequireFinite(t.ApplyAngleDeg(new VisionPoint2D(pose.X,pose.Y),pose.ThetaDeg),"transformed pose.thetaDeg");
        var world=new VisionCoordinatePose2D(q.X,q.Y,angle,t.TargetFrame,t.TargetUnit);
        var diag=CoordinateValidation.Diagnostics(Type,("outputX",q.X),("outputY",q.Y),("outputThetaDeg",angle),("determinant",det),("targetFrame",t.TargetFrame),("unit",t.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"pose",VisionValue.CoordinatePose(world)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)},{"thetaDeg",VisionValue.Double(angle)},{"frame",VisionValue.String(t.TargetFrame)},{"unit",VisionValue.String(t.TargetUnit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"x",Math.Round(q.X,4)},{"y",Math.Round(q.Y,4)},{"thetaDeg",Math.Round(angle,4)},{"frame",t.TargetFrame},{"unit",t.TargetUnit},{"diagnostics",diag}}));
    }
}
public sealed class CoordinatePointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.point";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = CoordinateLiteral.Point(context, node, 0, 0, "Workpiece");
        var diag = CoordinateValidation.Diagnostics(Type, ("x", p.X), ("y", p.Y), ("frame", p.Frame), ("unit", p.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(p)},{"x",VisionValue.Double(p.X)},{"y",VisionValue.Double(p.Y)},{"frame",VisionValue.String(p.Frame)},{"unit",VisionValue.String(p.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"point",$"({p.X:0.###},{p.Y:0.###}) {p.Unit}"},{"frame",p.Frame},{"unit",p.Unit},{"diagnostics",diag}}));
    }
}
public sealed class CoordinateDistanceNode : IVisionNodeExecutor
{
    public string Type => "coordinate.distance";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a=CoordinateValidation.RequireFinitePoint(context.Require<VisionCoordinatePoint2D>("pointA"),"pointA");
        var b=CoordinateValidation.RequireFinitePoint(context.Require<VisionCoordinatePoint2D>("pointB"),"pointB");
        if(!string.Equals(a.Frame,b.Frame,StringComparison.OrdinalIgnoreCase)||!string.Equals(a.Unit,b.Unit,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Coordinate frame/unit mismatch: {a.Frame}/{a.Unit} vs {b.Frame}/{b.Unit}.");
        var dx=CoordinateValidation.RequireFinite(b.X-a.X,"distance.dx"); var dy=CoordinateValidation.RequireFinite(b.Y-a.Y,"distance.dy");
        var d=Math.Sqrt(dx*dx+dy*dy);
        if (!double.IsFinite(d)) throw new InvalidOperationException("coordinate.distance produced a non-finite distance.");
        var diag=CoordinateValidation.Diagnostics(Type,("dx",dx),("dy",dy),("distance",d),("frame",a.Frame),("unit",a.Unit),("coincident",d<1e-12));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"measurement",VisionValue.Measurement(new(d,a.Unit,"Distance"))},{"distance",VisionValue.Double(d)},{"dx",VisionValue.Double(dx)},{"dy",VisionValue.Double(dy)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"distance",Math.Round(d,4)},{"unit",a.Unit},{"frame",a.Frame},{"diagnostics",diag}}));
    }
}

internal static class CoordinateLiteral
{
    public static VisionCoordinatePoint2D Point(NodeExecutionContext context, NodeDefinition node, double defaultX, double defaultY, string defaultFrame)
    {
        var (x, y, frame, unit) = Read(context, node, defaultX, defaultY, defaultFrame, "coordinate.point");
        return new VisionCoordinatePoint2D(x, y, frame, unit);
    }

    public static VisionCoordinatePose2D Pose(NodeExecutionContext context, NodeDefinition node, double defaultX, double defaultY, double defaultTheta, string defaultFrame)
    {
        var (x, y, frame, unit) = Read(context, node, defaultX, defaultY, defaultFrame, "coordinate.pose");
        var theta = Number(context, node, "thetaDeg", defaultTheta, "coordinate.pose");
        return new VisionCoordinatePose2D(x, y, VisionTransform2D.NormalizeAngleDeg(theta), frame, unit);
    }

    private static (double X, double Y, string Frame, string Unit) Read(NodeExecutionContext context, NodeDefinition node, double defaultX, double defaultY, string defaultFrame, string nodeType)
    {
        var x = Number(context, node, "x", defaultX, nodeType);
        var y = Number(context, node, "y", defaultY, nodeType);
        var frame = node.GetString("frame", defaultFrame).Trim();
        if (frame.Length == 0)
            throw new InvalidOperationException($"{nodeType} frame must be non-empty. Connect a frame tree to check the name against known frames.");
        var unitText = node.GetString("unit", "mm");
        var unit = unitText.Trim().ToLowerInvariant();
        if (unit is not ("mm" or "cm" or "m"))
            throw new InvalidOperationException($"{nodeType} unit '{unitText}' is not supported. Allowed units: mm, cm, m.");
        if (context.TryGetValue("tree", out var treeValue))
        {
            var tree = treeValue.Require<VisionFrameTree2D>("tree");
            var known = tree.Transforms
                .SelectMany(transform => new[] { transform.SourceFrame, transform.TargetFrame })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!known.Any(name => name.Equals(frame, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{nodeType} frame '{frame}' is not in the connected frame tree. Known frames: {string.Join(", ", known)}.");
        }
        return (x, y, frame, unit);
    }

    private static double Number(NodeExecutionContext context, NodeDefinition node, string name, double fallback, string nodeType)
    {
        var value = context.TryGetValue(name, out _) ? context.RequireNumber(name) : node.GetDouble(name, fallback);
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"{nodeType} {name} must be a finite number.");
        return value;
    }
}
