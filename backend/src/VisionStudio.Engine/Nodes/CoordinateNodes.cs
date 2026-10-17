using System.Text.Json;
using VisionStudio.Engine.Runtime;
namespace VisionStudio.Engine.Nodes;
public sealed record CalibrationPair(double ImageX, double ImageY, double WorldX, double WorldY);
public static class CalibrationMath
{
    public static VisionTransform2D SolveHomography(IReadOnlyList<CalibrationPair> pairs, string sourceFrame, string targetFrame, string unit)
    {
        if (pairs.Count < 4) throw new InvalidOperationException("Planar calibration requires at least 4 point pairs.");
        foreach (var p in pairs) if (!double.IsFinite(p.ImageX) || !double.IsFinite(p.ImageY) || !double.IsFinite(p.WorldX) || !double.IsFinite(p.WorldY)) throw new InvalidOperationException("Planar calibration pairs must all be finite.");
        var ata = new double[8,8]; var atb = new double[8];
        foreach (var p in pairs) { AddRow([p.ImageX,p.ImageY,1,0,0,0,-p.WorldX*p.ImageX,-p.WorldX*p.ImageY], p.WorldX, ata, atb); AddRow([0,0,0,p.ImageX,p.ImageY,1,-p.WorldY*p.ImageX,-p.WorldY*p.ImageY], p.WorldY, ata, atb); }
        var h = Solve(ata, atb);
        return new VisionTransform2D(sourceFrame,targetFrame,unit,h[0],h[1],h[2],h[3],h[4],h[5],h[6],h[7],1);
    }
    private static void AddRow(double[] row, double b, double[,] ata, double[] atb) { for (var i=0;i<8;i++) { atb[i]+=row[i]*b; for (var j=0;j<8;j++) ata[i,j]+=row[i]*row[j]; } }
    private static double[] Solve(double[,] a, double[] b)
    {
        var n=b.Length; var aug=new double[n,n+1];
        for(var r=0;r<n;r++){for(var c=0;c<n;c++)aug[r,c]=a[r,c];aug[r,n]=b[r];}
        for(var col=0;col<n;col++) { var pivot=col; for(var r=col+1;r<n;r++) if(Math.Abs(aug[r,col])>Math.Abs(aug[pivot,col])) pivot=r; if(Math.Abs(aug[pivot,col])<1e-12) throw new InvalidOperationException("Calibration point geometry is singular or poorly conditioned."); if(pivot!=col) for(var c=col;c<=n;c++) (aug[col,c],aug[pivot,c])=(aug[pivot,c],aug[col,c]); var div=aug[col,col]; for(var c=col;c<=n;c++) aug[col,c]/=div; for(var r=0;r<n;r++) if(r!=col){var f=aug[r,col]; for(var c=col;c<=n;c++) aug[r,c]-=f*aug[col,c];} }
        var x=new double[n]; for(var i=0;i<n;i++) x[i]=aug[i,n]; return x;
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
        var transform=CalibrationMath.SolveHomography(pairs,source,target,unit);
        var det=CoordinateValidation.ValidateTransform(transform,"calibration.planar result");
        var errors=pairs.Select(p=>{var q=transform.Apply(new VisionPoint2D(p.ImageX,p.ImageY)); return Math.Sqrt(Math.Pow(q.X-p.WorldX,2)+Math.Pow(q.Y-p.WorldY,2));}).ToArray();
        if (errors.Any(e=>!double.IsFinite(e))) throw new InvalidOperationException("calibration.planar produced non-finite residuals.");
        var rmse=Math.Sqrt(errors.Select(e=>e*e).Average()); var max=errors.Max(); var worst=Array.IndexOf(errors,max);
        var diag=CoordinateValidation.Diagnostics(Type,("pointCount",pairs.Count),("rmse",rmse),("maxError",max),("worstPairIndex",worst),("determinant",det),("sourceFrame",source),("targetFrame",target),("unit",unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"rmse",VisionValue.Double(rmse)},{"maxError",VisionValue.Double(max)},{"pointCount",VisionValue.Integer(pairs.Count)},{"diagnostics",VisionValue.String(diag)}},
            new Dictionary<string,object?>{{"points",pairs.Count},{"sourceFrame",source},{"targetFrame",target},{"unit",unit},{"assetId",string.IsNullOrWhiteSpace(node.GetString("assetId",""))?null:node.GetString("assetId","")},{"assetVersion",assetVersion>0?assetVersion:null},{"rmse",Math.Round(rmse,6)},{"maxError",Math.Round(max,6)},{"worstPairIndex",worst},{"diagnostics",diag}}));
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
        var x=CoordinateValidation.RequireFinite(node.GetDouble("x",0),"x");
        var y=CoordinateValidation.RequireFinite(node.GetDouble("y",0),"y");
        var frame=CoordinateValidation.RequireFrame(node.GetString("frame","Workpiece"),"frame");
        var unit=CoordinateValidation.RequireUnit(node.GetString("unit","mm"),"unit","mm","cm","m","px");
        var p=new VisionCoordinatePoint2D(x,y,frame,unit);
        var diag=CoordinateValidation.Diagnostics(Type,("x",x),("y",y),("frame",frame),("unit",unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(p)},{"x",VisionValue.Double(x)},{"y",VisionValue.Double(y)},{"frame",VisionValue.String(frame)},{"unit",VisionValue.String(unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"point",$"({p.X:0.###},{p.Y:0.###}) {p.Unit}"},{"frame",p.Frame},{"diagnostics",diag}}));
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
