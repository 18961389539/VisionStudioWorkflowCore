using System.Text.Json;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed record CalibrationPair(double ImageX, double ImageY, double WorldX, double WorldY);

public static class CalibrationMath
{
    public static VisionTransform2D SolveHomography(IReadOnlyList<CalibrationPair> pairs, string sourceFrame, string targetFrame, string unit)
    {
        if (pairs.Count < 4) throw new InvalidOperationException("Planar calibration requires at least 4 point pairs.");
        var ata = new double[8,8]; var atb = new double[8];
        foreach (var p in pairs)
        {
            AddRow([p.ImageX,p.ImageY,1,0,0,0,-p.WorldX*p.ImageX,-p.WorldX*p.ImageY], p.WorldX, ata, atb);
            AddRow([0,0,0,p.ImageX,p.ImageY,1,-p.WorldY*p.ImageX,-p.WorldY*p.ImageY], p.WorldY, ata, atb);
        }
        var h = Solve(ata, atb);
        return new VisionTransform2D(sourceFrame,targetFrame,unit,h[0],h[1],h[2],h[3],h[4],h[5],h[6],h[7],1);
    }

    private static void AddRow(double[] row, double b, double[,] ata, double[] atb)
    {
        for (var i=0;i<8;i++) { atb[i]+=row[i]*b; for (var j=0;j<8;j++) ata[i,j]+=row[i]*row[j]; }
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        var n=b.Length; var aug=new double[n,n+1];
        for(var r=0;r<n;r++){for(var c=0;c<n;c++)aug[r,c]=a[r,c];aug[r,n]=b[r];}
        for(var col=0;col<n;col++)
        {
            var pivot=col; for(var r=col+1;r<n;r++) if(Math.Abs(aug[r,col])>Math.Abs(aug[pivot,col])) pivot=r;
            if(Math.Abs(aug[pivot,col])<1e-12) throw new InvalidOperationException("Calibration point geometry is singular or poorly conditioned.");
            if(pivot!=col) for(var c=col;c<=n;c++) (aug[col,c],aug[pivot,c])=(aug[pivot,c],aug[col,c]);
            var div=aug[col,col]; for(var c=col;c<=n;c++) aug[col,c]/=div;
            for(var r=0;r<n;r++) if(r!=col){var f=aug[r,col]; for(var c=col;c<=n;c++) aug[r,c]-=f*aug[col,c];}
        }
        var x=new double[n]; for(var i=0;i<n;i++) x[i]=aug[i,n]; return x;
    }
}

public sealed class PlanarCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.planar";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var json=node.GetString("pairsJson","[]");
        var pairs=JsonSerializer.Deserialize<List<CalibrationPair>>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? [];
        var source=node.GetString("sourceFrame","ImagePixel"); var target=node.GetString("targetFrame","Workpiece"); var unit=node.GetString("targetUnit","mm");
        var assetId=node.GetString("assetId",""); var assetVersion=node.GetInt("assetVersion",0);
        var transform=CalibrationMath.SolveHomography(pairs,source,target,unit);
        var errors=pairs.Select(p=>{var q=transform.Apply(new VisionPoint2D(p.ImageX,p.ImageY)); return Math.Sqrt(Math.Pow(q.X-p.WorldX,2)+Math.Pow(q.Y-p.WorldY,2));}).ToArray();
        var rmse=Math.Sqrt(errors.Select(e=>e*e).Average()); var max=errors.Max();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"rmse",VisionValue.Double(rmse)},{"maxError",VisionValue.Double(max)}},
            new Dictionary<string,object?>{{"points",pairs.Count},{"sourceFrame",source},{"targetFrame",target},{"unit",unit},{"assetId",string.IsNullOrWhiteSpace(assetId)?null:assetId},{"assetVersion",assetVersion>0?assetVersion:null},{"rmse",Math.Round(rmse,6)},{"maxError",Math.Round(max,6)}}));
    }
}

public sealed class TransformPointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformPoint";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var p=context.Require<VisionPoint2D>("point"); var t=context.Require<VisionTransform2D>("transform"); var q=t.Apply(p);
        var world=new VisionCoordinatePoint2D(q.X,q.Y,t.TargetFrame,t.TargetUnit);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(world)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)}},new Dictionary<string,object?>{{"point",$"({q.X:0.###},{q.Y:0.###}) {t.TargetUnit}"},{"frame",t.TargetFrame}}));
    }
}

public sealed class TransformLineNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformLine";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var line=context.Require<VisionLine2D>("line"); var t=context.Require<VisionTransform2D>("transform");
        var a=t.Apply(line.Start); var b=t.Apply(line.End);
        var wa=new VisionCoordinatePoint2D(a.X,a.Y,t.TargetFrame,t.TargetUnit); var wb=new VisionCoordinatePoint2D(b.X,b.Y,t.TargetFrame,t.TargetUnit);
        var wl=new VisionCoordinateLine2D(wa,wb); var m=new VisionMeasurement(wl.Length,t.TargetUnit,"Distance");
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"line",VisionValue.CoordinateLine(wl)},{"length",VisionValue.Measurement(m)},{"lengthValue",VisionValue.Double(wl.Length)}},new Dictionary<string,object?>{{"length",Math.Round(wl.Length,4)},{"unit",t.TargetUnit},{"frame",t.TargetFrame}}));
    }
}

public sealed class TransformPoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformPose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var pose=context.Require<VisionPose2D>("pose"); var t=context.Require<VisionTransform2D>("transform"); var q=t.Apply(new VisionPoint2D(pose.X,pose.Y)); var angle=t.ApplyAngleDeg(new VisionPoint2D(pose.X,pose.Y),pose.ThetaDeg);
        var world=new VisionCoordinatePose2D(q.X,q.Y,angle,t.TargetFrame,t.TargetUnit);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"pose",VisionValue.CoordinatePose(world)}},new Dictionary<string,object?>{{"x",Math.Round(q.X,4)},{"y",Math.Round(q.Y,4)},{"thetaDeg",Math.Round(angle,4)},{"frame",t.TargetFrame},{"unit",t.TargetUnit}}));
    }
}

public sealed class CoordinatePointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.point";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var p=new VisionCoordinatePoint2D(node.GetDouble("x",0),node.GetDouble("y",0),node.GetString("frame","Workpiece"),node.GetString("unit","mm"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(p)}},new Dictionary<string,object?>{{"point",$"({p.X:0.###},{p.Y:0.###}) {p.Unit}"},{"frame",p.Frame}}));
    }
}

public sealed class CoordinateDistanceNode : IVisionNodeExecutor
{
    public string Type => "coordinate.distance";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a=context.Require<VisionCoordinatePoint2D>("pointA"); var b=context.Require<VisionCoordinatePoint2D>("pointB");
        if(!string.Equals(a.Frame,b.Frame,StringComparison.OrdinalIgnoreCase)||!string.Equals(a.Unit,b.Unit,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Coordinate frame/unit mismatch: {a.Frame}/{a.Unit} vs {b.Frame}/{b.Unit}.");
        var d=Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2)); var m=new VisionMeasurement(d,a.Unit,"Distance");
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"measurement",VisionValue.Measurement(m)},{"distance",VisionValue.Double(d)}},new Dictionary<string,object?>{{"distance",Math.Round(d,4)},{"unit",a.Unit},{"frame",a.Frame}}));
    }
}
