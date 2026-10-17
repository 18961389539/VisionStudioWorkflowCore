using System.Text.Json;
using VisionStudio.Engine.Runtime;
namespace VisionStudio.Engine.Nodes;
public sealed record FrameTransformDefinition(string SourceFrame,string TargetFrame,string Unit,double X,double Y,double AngleDeg);
public sealed class ConstantTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformConstant";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Workpiece"),"sourceFrame");
        var target=CoordinateValidation.RequireFrame(node.GetString("targetFrame","RobotBase"),"targetFrame");
        var unit=CoordinateValidation.RequireUnit(node.GetString("unit","mm"),"unit","px","mm","cm","m");
        var x=CoordinateValidation.RequireFinite(node.GetDouble("x",0),"x");
        var y=CoordinateValidation.RequireFinite(node.GetDouble("y",0),"y");
        var angle=CoordinateValidation.RequireFinite(node.GetDouble("angleDeg",0),"angleDeg");
        var transform=VisionTransform2DMath.Rigid(source,target,unit,x,y,angle);
        var det=CoordinateValidation.ValidateTransform(transform,"transformConstant");
        var diag=CoordinateValidation.Diagnostics(Type,("x",x),("y",y),("angleDeg",angle),("determinant",det),("sourceFrame",source),("targetFrame",target),("unit",unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"x",VisionValue.Double(x)},{"y",VisionValue.Double(y)},{"angleDeg",VisionValue.Double(angle)},{"sourceFrame",VisionValue.String(source)},{"targetFrame",VisionValue.String(target)},{"unit",VisionValue.String(unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{source} -> {target}"},{"x",x},{"y",y},{"angleDeg",angle},{"unit",unit},{"diagnostics",diag}}));
    }
}
public sealed class InverseTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformInverse";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input=context.Require<VisionTransform2D>("transform");
        var det=CoordinateValidation.ValidateTransform(input,"transform");
        var output=VisionTransform2DMath.Inverse(input);
        var outDet=CoordinateValidation.ValidateTransform(output,"inverse transform");
        var diag=CoordinateValidation.Diagnostics(Type,("inputDeterminant",det),("outputDeterminant",outDet),("sourceFrame",output.SourceFrame),("targetFrame",output.TargetFrame),("unit",output.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(output)},{"sourceFrame",VisionValue.String(output.SourceFrame)},{"targetFrame",VisionValue.String(output.TargetFrame)},{"determinant",VisionValue.Double(outDet)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{output.SourceFrame} -> {output.TargetFrame}"},{"diagnostics",diag}}));
    }
}
public sealed class ComposeTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCompose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a=context.Require<VisionTransform2D>("transformA"); var b=context.Require<VisionTransform2D>("transformB");
        CoordinateValidation.ValidateTransform(a,"transformA"); CoordinateValidation.ValidateTransform(b,"transformB");
        var output=VisionTransform2DMath.Compose(a,b);
        var det=CoordinateValidation.ValidateTransform(output,"composed transform");
        var diag=CoordinateValidation.Diagnostics(Type,("determinant",det),("sourceFrame",output.SourceFrame),("viaFrame",a.TargetFrame),("targetFrame",output.TargetFrame),("unit",output.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(output)},{"sourceFrame",VisionValue.String(output.SourceFrame)},{"targetFrame",VisionValue.String(output.TargetFrame)},{"determinant",VisionValue.Double(det)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{output.SourceFrame} -> {output.TargetFrame}"},{"via",a.TargetFrame},{"diagnostics",diag}}));
    }
}
public sealed class FrameTreeNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameTree";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<FrameTransformDefinition> defs;
        try { defs=JsonSerializer.Deserialize<List<FrameTransformDefinition>>(node.GetString("transformsJson","[]"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"coordinate.frameTree transformsJson is not valid JSON: {ex.Message}"); }
        if (defs.Count==0) throw new InvalidOperationException("coordinate.frameTree requires at least one transform in transformsJson.");
        if (defs.Count>256) throw new InvalidOperationException("coordinate.frameTree accepts at most 256 transforms.");
        foreach (var d in defs) { CoordinateValidation.RequireFrame(d.SourceFrame,"transformsJson.sourceFrame"); CoordinateValidation.RequireFrame(d.TargetFrame,"transformsJson.targetFrame"); CoordinateValidation.RequireUnit(d.Unit,"transformsJson.unit","px","mm","cm","m"); CoordinateValidation.RequireFinite(d.X,"transformsJson.x"); CoordinateValidation.RequireFinite(d.Y,"transformsJson.y"); CoordinateValidation.RequireFinite(d.AngleDeg,"transformsJson.angleDeg"); }
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs) if (!seen.Add($"{d.SourceFrame}->{d.TargetFrame}")) throw new InvalidOperationException($"coordinate.frameTree duplicate edge {d.SourceFrame}->{d.TargetFrame}.");
        var transforms=defs.Select(x=>VisionTransform2DMath.Rigid(x.SourceFrame,x.TargetFrame,x.Unit,x.X,x.Y,x.AngleDeg)).ToArray();
        foreach (var t in transforms) CoordinateValidation.ValidateTransform(t,"frameTree transform");
        var tree=new VisionFrameTree2D(transforms);
        var frames=transforms.SelectMany(x=>new[]{x.SourceFrame,x.TargetFrame}).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        var diag=CoordinateValidation.Diagnostics(Type,("transformCount",transforms.Length),("frameCount",frames.Length),("frames",string.Join(",",frames)));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"tree",VisionValue.FrameTree(tree)},{"transformCount",VisionValue.Integer(transforms.Length)},{"frameCount",VisionValue.Integer(frames.Length)},{"frames",VisionValue.String(string.Join(",",frames))},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transforms",transforms.Length},{"frames",frames},{"diagnostics",diag}}));
    }
}
public sealed class ResolveFrameTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameResolve";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tree=context.Require<VisionFrameTree2D>("tree");
        var source=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Workpiece"),"sourceFrame");
        var target=CoordinateValidation.RequireFrame(node.GetString("targetFrame","RobotBase"),"targetFrame");
        var result=tree.Resolve(source,target);
        var det=CoordinateValidation.ValidateTransform(result.Transform,"resolved transform");
        var path=string.Join(" -> ",result.Path);
        var diag=CoordinateValidation.Diagnostics(Type,("hopCount",Math.Max(0,result.Path.Count-1)),("determinant",det),("path",path),("unit",result.Transform.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(result.Transform)},{"path",VisionValue.String(path)},{"hopCount",VisionValue.Integer(Math.Max(0,result.Path.Count-1))},{"sourceFrame",VisionValue.String(source)},{"targetFrame",VisionValue.String(target)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"path",path},{"sourceUnit",result.Transform.SourceUnit},{"targetUnit",result.Transform.TargetUnit},{"diagnostics",diag}}));
    }
}
public sealed class CoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.pose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawTheta = context.TryGetValue("thetaDeg", out _) ? context.RequireNumber("thetaDeg") : node.GetDouble("thetaDeg", 0);
        var pose = CoordinateLiteral.Pose(context, node, 0, 0, 0, "RobotBase");
        var diag = CoordinateValidation.Diagnostics(Type, ("x", pose.X), ("y", pose.Y), ("thetaDeg", rawTheta), ("normalizedThetaDeg", pose.ThetaDeg), ("frame", pose.Frame), ("unit", pose.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["pose"] = VisionValue.CoordinatePose(pose), ["x"] = VisionValue.Double(pose.X), ["y"] = VisionValue.Double(pose.Y), ["thetaDeg"] = VisionValue.Double(rawTheta), ["frame"] = VisionValue.String(pose.Frame), ["unit"] = VisionValue.String(pose.Unit), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["pose"] = $"X={pose.X:0.###}, Y={pose.Y:0.###}, R={pose.ThetaDeg:0.###}", ["frame"] = pose.Frame, ["unit"] = pose.Unit, ["diagnostics"] = diag }));
    }
}
public sealed class PoseToTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.poseToTransform";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var sourceFrame=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Tool"),"sourceFrame");
        if (sourceFrame.Equals(pose.Frame,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("coordinate.poseToTransform sourceFrame must differ from the pose frame.");
        var transform=VisionTransform2DMath.Rigid(sourceFrame,pose.Frame,pose.Unit,pose.X,pose.Y,pose.ThetaDeg); var det=CoordinateValidation.ValidateTransform(transform,"pose transform");
        var diag=CoordinateValidation.Diagnostics(Type,("x",pose.X),("y",pose.Y),("thetaDeg",pose.ThetaDeg),("determinant",det),("sourceFrame",sourceFrame),("targetFrame",pose.Frame));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"x",VisionValue.Double(pose.X)},{"y",VisionValue.Double(pose.Y)},{"thetaDeg",VisionValue.Double(pose.ThetaDeg)},{"sourceFrame",VisionValue.String(sourceFrame)},{"targetFrame",VisionValue.String(pose.Frame)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{sourceFrame} -> {pose.Frame}"},{"x",pose.X},{"y",pose.Y},{"angleDeg",pose.ThetaDeg},{"diagnostics",diag}}));
    }
}
public sealed class TransformCoordinatePointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePoint";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var point=CoordinateValidation.RequireFinitePoint(context.Require<VisionCoordinatePoint2D>("point"),"point");
        var transform=context.Require<VisionTransform2D>("transform"); var det=CoordinateValidation.ValidateTransform(transform,"transform");
        var q=CoordinateValidation.RequireFinitePoint(VisionTransform2DMath.Apply(transform,point),"transformed point");
        var diag=CoordinateValidation.Diagnostics(Type,("inputX",point.X),("inputY",point.Y),("outputX",q.X),("outputY",q.Y),("determinant",det),("targetFrame",q.Frame),("unit",q.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"point",VisionValue.CoordinatePoint(q)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)},{"frame",VisionValue.String(q.Frame)},{"unit",VisionValue.String(q.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"point",$"({q.X:0.###},{q.Y:0.###}) {q.Unit}"},{"frame",q.Frame},{"diagnostics",diag}}));
    }
}
public sealed class TransformCoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var transform=context.Require<VisionTransform2D>("transform"); var det=CoordinateValidation.ValidateTransform(transform,"transform");
        var q=CoordinateValidation.RequireFinitePose(VisionTransform2DMath.Apply(transform,pose),"transformed pose");
        var diag=CoordinateValidation.Diagnostics(Type,("inputThetaDeg",pose.ThetaDeg),("outputX",q.X),("outputY",q.Y),("outputThetaDeg",q.ThetaDeg),("determinant",det),("targetFrame",q.Frame),("unit",q.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"pose",VisionValue.CoordinatePose(q)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)},{"thetaDeg",VisionValue.Double(q.ThetaDeg)},{"frame",VisionValue.String(q.Frame)},{"unit",VisionValue.String(q.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"pose",$"X={q.X:0.###}, Y={q.Y:0.###}, R={q.ThetaDeg:0.###}"},{"frame",q.Frame},{"unit",q.Unit},{"diagnostics",diag}}));
    }
}
public sealed class RobotGuidance2DNode : IVisionNodeExecutor
{
    public string Type => "robot.guidance2d";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var expectedFrame=CoordinateValidation.RequireFrame(node.GetString("expectedFrame","RobotBase"),"expectedFrame");
        if (!pose.Frame.Equals(expectedFrame, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Robot guidance expected pose in '{expectedFrame}', got '{pose.Frame}'. Resolve the frame transform first.");
        var offsetX=CoordinateValidation.RequireFinite(node.GetDouble("offsetX",0),"offsetX"); var offsetY=CoordinateValidation.RequireFinite(node.GetDouble("offsetY",0),"offsetY"); var angleOffset=CoordinateValidation.RequireFinite(node.GetDouble("angleOffsetDeg",0),"angleOffsetDeg");
        var robot=CoordinateValidation.RequireNonEmpty(node.GetString("robot","ABB"),"robot"); var mode=CoordinateValidation.RequireNonEmpty(node.GetString("guidanceMode","EyeToHand"),"guidanceMode");
        if (mode is not ("EyeToHand" or "EyeInHand" or "Manual")) throw new InvalidOperationException($"robot.guidance2d guidanceMode '{mode}' is unsupported; use EyeToHand, EyeInHand or Manual.");
        var rad=pose.ThetaDeg*Math.PI/180.0; var x=pose.X+Math.Cos(rad)*offsetX-Math.Sin(rad)*offsetY; var y=pose.Y+Math.Sin(rad)*offsetX+Math.Cos(rad)*offsetY;
        CoordinateValidation.RequireFinite(x,"guidance.x"); CoordinateValidation.RequireFinite(y,"guidance.y");
        var r=VisionTransform2D.NormalizeAngleDeg(pose.ThetaDeg+angleOffset); var offsetDistance=Math.Sqrt(offsetX*offsetX+offsetY*offsetY);
        var target=new VisionRobotTarget2D(x,y,r,pose.Frame,pose.Unit,robot,mode);
        var diag=CoordinateValidation.Diagnostics(Type,("x",x),("y",y),("rDeg",r),("offsetDistance",offsetDistance),("frame",pose.Frame),("unit",pose.Unit),("robot",robot),("mode",mode));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"target",VisionValue.RobotTarget(target)},{"x",VisionValue.Double(x)},{"y",VisionValue.Double(y)},{"r",VisionValue.Double(r)},{"frame",VisionValue.String(pose.Frame)},{"unit",VisionValue.String(pose.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"robot",robot},{"mode",mode},{"target",$"X={x:0.###}, Y={y:0.###}, R={r:0.###}"},{"frame",pose.Frame},{"unit",pose.Unit},{"diagnostics",diag}}));
    }
}
public sealed class J4TcpCompensationNode : IVisionNodeExecutor
{
    public string Type => "robot.j4TcpCompensation";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target=context.Require<VisionRobotTarget2D>("target");
        CoordinateValidation.RequireFinite(target.X,"target.x"); CoordinateValidation.RequireFinite(target.Y,"target.y"); CoordinateValidation.RequireFinite(target.RDeg,"target.rDeg");
        CoordinateValidation.RequireFrame(target.Frame,"target.frame"); CoordinateValidation.RequireUnit(target.Unit,"target.unit");
        var tcpX=CoordinateValidation.RequireFinite(node.GetDouble("tcpOffsetX",0),"tcpOffsetX"); var tcpY=CoordinateValidation.RequireFinite(node.GetDouble("tcpOffsetY",0),"tcpOffsetY");
        var pivotX=CoordinateValidation.RequireFinite(node.GetDouble("j4PivotOffsetX",0),"j4PivotOffsetX"); var pivotY=CoordinateValidation.RequireFinite(node.GetDouble("j4PivotOffsetY",0),"j4PivotOffsetY");
        var zero=CoordinateValidation.RequireFinite(node.GetDouble("angleZeroOffsetDeg",0),"angleZeroOffsetDeg");
        var commandR=VisionTransform2D.NormalizeAngleDeg(target.RDeg+zero); var ex=tcpX-pivotX; var ey=tcpY-pivotY; var ecc=Math.Sqrt(ex*ex+ey*ey);
        var rad=commandR*Math.PI/180.0; var rotatedX=Math.Cos(rad)*ex-Math.Sin(rad)*ey; var rotatedY=Math.Sin(rad)*ex+Math.Cos(rad)*ey;
        var commandX=CoordinateValidation.RequireFinite(target.X-rotatedX,"command.x"); var commandY=CoordinateValidation.RequireFinite(target.Y-rotatedY,"command.y");
        var compensated=target with { X=commandX, Y=commandY, RDeg=commandR };
        var diag=CoordinateValidation.Diagnostics(Type,("commandX",commandX),("commandY",commandY),("commandRDeg",commandR),("eccentricity",ecc),("eccentricityX",ex),("eccentricityY",ey),("frame",target.Frame),("unit",target.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"target",VisionValue.RobotTarget(compensated)},{"x",VisionValue.Double(commandX)},{"y",VisionValue.Double(commandY)},{"r",VisionValue.Double(commandR)},{"eccentricity",VisionValue.Double(ecc)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"desiredTcp",$"X={target.X:0.###}, Y={target.Y:0.###}, R={target.RDeg:0.###}"},{"commandPivot",$"X={commandX:0.###}, Y={commandY:0.###}, R={commandR:0.###}"},{"eccentricity",$"({ex:0.###},{ey:0.###}) {target.Unit}"},{"diagnostics",diag}}));
    }
}
