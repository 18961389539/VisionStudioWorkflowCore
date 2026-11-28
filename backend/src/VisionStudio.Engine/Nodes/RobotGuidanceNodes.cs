using System.Text.Json;
namespace VisionStudio.Engine.Nodes;
public sealed class ConstantTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformConstant";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var source=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Workpiece"),"sourceFrame");
        var target=CoordinateValidation.RequireFrame(node.GetString("targetFrame","RobotBase"),"targetFrame");
        var unit=CoordinateValidation.RequireUnit(node.GetString("unit","mm"),"unit","px","mm","cm","m");
        var x=CoordinateValidation.RequireFinite(node.GetDouble("x",0),"x");
        var y=CoordinateValidation.RequireFinite(node.GetDouble("y",0),"y");
        var angle=CoordinateValidation.RequireFinite(node.GetDouble("angleDeg",0),"angleDeg");
        var normalizedAngle=VisionTransform2D.NormalizeAngleDeg(angle);
        var transform=VisionTransform2DMath.Rigid(source,target,unit,x,y,normalizedAngle);
        var det=CoordinateValidation.ValidateTransform(transform,"transformConstant");
        var diag=CoordinateValidation.Diagnostics(Type,("x",x),("y",y),("angleDeg",angle),("normalizedAngleDeg",normalizedAngle),("angleInterval","(-180,180]"),("determinant",det),("sourceFrame",source),("targetFrame",target),("unit",unit),("unitContract","same source and target unit; no unit conversion"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"x",VisionValue.Double(x)},{"y",VisionValue.Double(y)},{"angleDeg",VisionValue.Double(normalizedAngle)},{"sourceFrame",VisionValue.String(source)},{"targetFrame",VisionValue.String(target)},{"unit",VisionValue.String(unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{source} -> {target}"},{"x",x},{"y",y},{"angleDeg",normalizedAngle},{"unit",unit},{"diagnostics",diag}}));
    }
}
public sealed class InverseTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformInverse";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var input = context.Require<VisionTransform2D>("transform");
        CoordinateValidation.RequireFrame(input.SourceFrame, "transform.sourceFrame");
        CoordinateValidation.RequireFrame(input.TargetFrame, "transform.targetFrame");
        CoordinateValidation.RequireUnit(input.SourceUnit, "transform.sourceUnit");
        CoordinateValidation.RequireUnit(input.TargetUnit, "transform.targetUnit");
        var det = CoordinateValidation.ValidateTransform(input, "transform");
        var threshold = CoordinateValidation.SingularityThreshold(input);
        if (Math.Abs(det) < threshold)
            throw new InvalidOperationException($"coordinate.transformInverse rejects a singular or ill-conditioned transform: determinant={det:0.####}, threshold={threshold:0.####}, matrix=[{string.Join(",",input.Matrix)}].");
        var condition = CoordinateValidation.ConditionNumber(input);
        if (!double.IsFinite(condition) || condition > 1e12)
            throw new InvalidOperationException($"coordinate.transformInverse condition number {condition} exceeds 1e12; determinant={det:0.####}, threshold={threshold:0.####}.");
        var output = VisionTransform2DMath.Inverse(input);
        var outDet = CoordinateValidation.ValidateTransform(output, "inverse transform");
        if (Math.Abs(outDet) < 1e-12)
            throw new InvalidOperationException("coordinate.transformInverse produced a singular inverse.");
        CoordinateValidation.RequireFrame(output.SourceFrame, "inverse.sourceFrame");
        CoordinateValidation.RequireFrame(output.TargetFrame, "inverse.targetFrame");
        var diag = CoordinateValidation.Diagnostics(Type,
            ("inputDeterminant", det), ("outputDeterminant", outDet),
            ("sourceFrame", output.SourceFrame), ("targetFrame", output.TargetFrame),
            ("unit", output.TargetUnit), ("inputSourceFrame", input.SourceFrame), ("inputTargetFrame", input.TargetFrame),
            ("singularityThreshold", threshold), ("conditionNumber", condition), ("matrixType", CoordinateValidation.MatrixType(input)),
            ("convention", "column vectors; active rotation"), ("matrix", input.Matrix));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["transform"] = VisionValue.Transform(output),
                ["sourceFrame"] = VisionValue.String(output.SourceFrame),
                ["targetFrame"] = VisionValue.String(output.TargetFrame),
                ["determinant"] = VisionValue.Double(outDet),
                ["inputDeterminant"] = VisionValue.Double(det),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["transform"] = $"{output.SourceFrame} -> {output.TargetFrame}",
                ["determinant"] = outDet,
                ["inputDeterminant"] = det,
                ["diagnostics"] = diag
            }));
    }
}
public sealed class ComposeTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCompose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var a=context.Require<VisionTransform2D>("transformA"); var b=context.Require<VisionTransform2D>("transformB");
        var detA=CoordinateValidation.ValidateTransform(a,"transformA"); var detB=CoordinateValidation.ValidateTransform(b,"transformB");
        var order=node.GetString("order","outerInner").Trim().ToLowerInvariant();
        if (order is not ("outerinner" or "innerouter")) throw new InvalidOperationException("coordinate.transformCompose order must be outerInner or innerOuter.");
        var expectedIntermediate=order=="outerinner"?b.SourceFrame:a.SourceFrame;
        var actualIntermediate=order=="outerinner"?a.TargetFrame:b.TargetFrame;
        try { VisionTransform2DMath.RequireFrameUnitMatch(actualIntermediate,order=="outerinner"?a.TargetUnit:b.TargetUnit,expectedIntermediate,order=="outerinner"?b.SourceUnit:a.SourceUnit,"compose intermediate"); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"coordinate.transformCompose order={order}: A={a.SourceFrame}->{a.TargetFrame}, B={b.SourceFrame}->{b.TargetFrame}; expected intermediate='{expectedIntermediate}', actual='{actualIntermediate}'. {ex.Message}",ex); }
        var output=order switch
        {
            "outerinner" => VisionTransform2DMath.Compose(a,b),
            _ => VisionTransform2DMath.Compose(b,a)
        };
        var det=CoordinateValidation.ValidateTransform(output,"composed transform");
        var diag=CoordinateValidation.Diagnostics(Type,("determinant",det),("inputDeterminantA",detA),("inputDeterminantB",detB),("order",order),("sourceFrame",output.SourceFrame),("viaFrame",actualIntermediate),("targetFrame",output.TargetFrame),("unit",output.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(output)},{"sourceFrame",VisionValue.String(output.SourceFrame)},{"targetFrame",VisionValue.String(output.TargetFrame)},{"determinant",VisionValue.Double(det)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{output.SourceFrame} -> {output.TargetFrame}"},{"via",a.TargetFrame},{"diagnostics",diag}}));
    }
}
public sealed class FrameTreeNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameTree";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

List<FrameTransformDefinition> defs;
        var schemaVersion=1;
        try
        {
            var transformsJson=node.GetString("transformsJson","[]");
            using var document=JsonDocument.Parse(transformsJson);
            JsonElement array;
            if (document.RootElement.ValueKind==JsonValueKind.Array) array=document.RootElement;
            else if (document.RootElement.ValueKind==JsonValueKind.Object && document.RootElement.TryGetProperty("schemaVersion",out var version))
            {
                if (!version.TryGetInt32(out schemaVersion) || schemaVersion!=1 || !document.RootElement.TryGetProperty("transforms",out array) || array.ValueKind!=JsonValueKind.Array)
                    throw new InvalidOperationException("coordinate.frameTree expects schemaVersion=1 and a transforms array.");
            }
            else throw new InvalidOperationException("coordinate.frameTree expects {schemaVersion:1,transforms:[...]} (legacy bare arrays are accepted).");
            defs=[];
            for (var i=0;i<array.GetArrayLength();i++)
            {
                var edge=array[i];
                var location=GetArrayElementLocation(transformsJson,i);
                if (edge.ValueKind!=JsonValueKind.Object) throw new InvalidOperationException($"transformsJson edge {i}: expected object at line {location.Line}, position {location.Position}.");
                try { defs.Add(edge.Deserialize<FrameTransformDefinition>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? throw new InvalidOperationException("null edge")); }
                catch (JsonException ex) { throw new InvalidOperationException($"transformsJson edge {i} ({string.Join(",",edge.EnumerateObject().Select(p=>p.Name))}): {ex.Message}; JSON line {ex.LineNumber}, position {ex.BytePositionInLine}.",ex); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"transformsJson edge {i}: {ex.Message}",ex); }
                foreach (var field in new[]{"sourceFrame","targetFrame","unit","x","y","angleDeg"})
                    if (!edge.TryGetProperty(field,out _)) throw new InvalidOperationException($"transformsJson edge {i}: missing field '{field}' at line {location.Line}, position {location.Position}.");
            }
        }
        catch (JsonException ex) { throw new InvalidOperationException($"coordinate.frameTree transformsJson syntax error at line {ex.LineNumber}, position {ex.BytePositionInLine}: {ex.Message}"); }
        if (defs.Count==0) throw new InvalidOperationException("coordinate.frameTree requires at least one transform in transformsJson.");
        if (defs.Count>256) throw new InvalidOperationException("coordinate.frameTree accepts at most 256 transforms.");
        foreach (var d in defs) { CoordinateValidation.RequireFrame(d.SourceFrame,"transformsJson.sourceFrame"); CoordinateValidation.RequireFrame(d.TargetFrame,"transformsJson.targetFrame"); CoordinateValidation.RequireUnit(d.Unit,"transformsJson.unit","px","mm","cm","m"); CoordinateValidation.RequireFinite(d.X,"transformsJson.x"); CoordinateValidation.RequireFinite(d.Y,"transformsJson.y"); CoordinateValidation.RequireFinite(d.AngleDeg,"transformsJson.angleDeg"); }
        var edges=new Dictionary<string,FrameTransformDefinition>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<defs.Count;i++)
        {
            var d=defs[i]; var key=$"{d.SourceFrame}->{d.TargetFrame}";
            if(edges.TryGetValue(key,out var existing))
            {
                var same=existing.Unit.Equals(d.Unit,StringComparison.OrdinalIgnoreCase)&&existing.X.Equals(d.X)&&existing.Y.Equals(d.Y)&&VisionTransform2D.NormalizeAngleDeg(existing.AngleDeg).Equals(VisionTransform2D.NormalizeAngleDeg(d.AngleDeg));
                if(!same) throw new InvalidOperationException($"coordinate.frameTree conflicting duplicate edge {d.SourceFrame}->{d.TargetFrame} at edge {i}; existing transform=({existing.X},{existing.Y},{existing.AngleDeg} {existing.Unit}), incoming=({d.X},{d.Y},{d.AngleDeg} {d.Unit}).");
                continue;
            }
            edges.Add(key,d);
        }
        var transforms=edges.Values.Select(x=>VisionTransform2DMath.Rigid(x.SourceFrame,x.TargetFrame,x.Unit,x.X,x.Y,x.AngleDeg)).ToArray();
        foreach (var t in transforms) CoordinateValidation.ValidateTransform(t,"frameTree transform");
        var tree=new VisionFrameTree2D(transforms);
        var frames=transforms.SelectMany(x=>new[]{x.SourceFrame,x.TargetFrame}).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        var framesJson=JsonSerializer.Serialize(frames);
        var diag=CoordinateValidation.Diagnostics(Type,("schemaVersion",schemaVersion),("duplicateEdgesRemoved",defs.Count-transforms.Length),("transformCount",transforms.Length),("frameCount",frames.Length),("frames",frames));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"tree",VisionValue.FrameTree(tree)},{"transformCount",VisionValue.Integer(transforms.Length)},{"frameCount",VisionValue.Integer(frames.Length)},{"frames",VisionValue.String(framesJson)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"schemaVersion",schemaVersion},{"transforms",transforms.Length},{"frames",frames},{"diagnostics",diag}}));
    }

    private static (long Line, long Position) GetArrayElementLocation(string json, int elementIndex)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        var targetDepth = -1;
        var currentIndex = -1;
        var transformsProperty = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                transformsProperty = reader.ValueTextEquals("transforms");
                continue;
            }
            if (targetDepth < 0 && reader.TokenType == JsonTokenType.StartArray && (reader.CurrentDepth == 0 || transformsProperty))
            {
                targetDepth = reader.CurrentDepth;
                continue;
            }
            if (targetDepth < 0 || reader.CurrentDepth != targetDepth || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.StartArray or JsonTokenType.EndObject)
                continue;
            transformsProperty = false;
            currentIndex++;
            if (currentIndex != elementIndex) continue;
            long line = 1, position = 0;
            for (var i = 0; i < reader.BytesConsumed; i++)
            {
                if (bytes[i] == (byte)'\n') { line++; position = 0; }
                else position++;
            }
            return (line, position);
        }
        return (0, 0);
    }
}
public sealed class ResolveFrameTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameResolve";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var tree=context.Require<VisionFrameTree2D>("tree");
        var source=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Workpiece"),"sourceFrame");
        var target=CoordinateValidation.RequireFrame(node.GetString("targetFrame","RobotBase"),"targetFrame");
        var frames=tree.Transforms.SelectMany(x=>new[]{x.SourceFrame,x.TargetFrame}).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        var unknown=new[]{source,target}.Where(name=>!frames.Contains(name,StringComparer.OrdinalIgnoreCase)).ToArray();
        if(unknown.Length>0) throw new InvalidOperationException($"coordinate.frameResolve unknown frame(s): {string.Join(",",unknown)}. Available frames: {string.Join(",",frames)}.");
        VisionFrameResolveResult result;
        try { result=tree.Resolve(source,target); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"coordinate.frameResolve no path from '{source}' to '{target}'. Available frames: {string.Join(",",frames)}.",ex); }
        var det=CoordinateValidation.ValidateTransform(result.Transform,"resolved transform");
        var path=string.Join(" -> ",result.Path);
        var diag=CoordinateValidation.Diagnostics(Type,("hopCount",Math.Max(0,result.Path.Count-1)),("determinant",det),("path",path),("candidatePaths",result.CandidatePaths),("selectionRule","lexicographically smallest equal-length path"),("unit",result.Transform.TargetUnit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(result.Transform)},{"path",VisionValue.String(path)},{"hopCount",VisionValue.Integer(Math.Max(0,result.Path.Count-1))},{"sourceFrame",VisionValue.String(source)},{"targetFrame",VisionValue.String(target)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"path",path},{"sourceUnit",result.Transform.SourceUnit},{"targetUnit",result.Transform.TargetUnit},{"diagnostics",diag}}));
    }
}
public sealed class CoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.pose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var rawTheta = context.TryGetValue("thetaDeg", out _) ? context.RequireNumber("thetaDeg") : node.GetDouble("thetaDeg", 0);
        CoordinateValidation.RequireFinite(rawTheta, "thetaDeg");
        var pose = CoordinateLiteral.Pose(context, node, 500, 200, 0, "RobotBase");
        CoordinateValidation.RequireFinitePose(pose, "pose");
        if (Math.Abs(pose.X) > 1e9 || Math.Abs(pose.Y) > 1e9)
            throw new InvalidOperationException("coordinate.pose translation must be within +/-1e9.");
        var diag = CoordinateValidation.Diagnostics(Type, ("x", pose.X), ("y", pose.Y), ("thetaDeg", rawTheta), ("normalizedThetaDeg", pose.ThetaDeg), ("frame", pose.Frame), ("unit", pose.Unit), ("frameValidation",CoordinateLiteral.FrameValidationState(context,pose.Frame)),("sources",CoordinateLiteral.Sources(context,"x","y","thetaDeg")),("angleInterval","(-180,180]"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["pose"] = VisionValue.CoordinatePose(pose), ["x"] = VisionValue.Double(pose.X), ["y"] = VisionValue.Double(pose.Y), ["thetaDeg"] = VisionValue.Double(pose.ThetaDeg), ["frame"] = VisionValue.String(pose.Frame), ["unit"] = VisionValue.String(pose.Unit), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["pose"] = $"X={pose.X:0.###}, Y={pose.Y:0.###}, R={pose.ThetaDeg:0.###}", ["frame"] = pose.Frame, ["unit"] = pose.Unit, ["diagnostics"] = diag }));
    }
}

public sealed class PoseToTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.poseToTransform";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var sourceFrame=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Tool"),"sourceFrame");
        if (sourceFrame.Equals(pose.Frame,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("coordinate.poseToTransform sourceFrame must differ from the pose frame.");
        var transform=VisionTransform2DMath.Rigid(sourceFrame,pose.Frame,pose.Unit,pose.X,pose.Y,pose.ThetaDeg); var det=CoordinateValidation.ValidateTransform(transform,"pose transform");
        var diag=CoordinateValidation.Diagnostics(Type,("x",pose.X),("y",pose.Y),("thetaDeg",pose.ThetaDeg),("determinant",det),("sourceFrame",sourceFrame),("targetFrame",pose.Frame),("unit",pose.Unit),("direction",$"{sourceFrame} -> {pose.Frame}"),("semantics","pose is expressed in its parent target frame"),("unitContract","pose unit reused without conversion"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"transform",VisionValue.Transform(transform)},{"x",VisionValue.Double(pose.X)},{"y",VisionValue.Double(pose.Y)},{"thetaDeg",VisionValue.Double(pose.ThetaDeg)},{"sourceFrame",VisionValue.String(sourceFrame)},{"targetFrame",VisionValue.String(pose.Frame)},{"unit",VisionValue.String(pose.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"transform",$"{sourceFrame} -> {pose.Frame}"},{"x",pose.X},{"y",pose.Y},{"angleDeg",pose.ThetaDeg},{"unit",pose.Unit},{"diagnostics",diag}}));
    }
}
public sealed class TransformCoordinatePointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePoint";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var point = CoordinateValidation.RequireFinitePoint(context.Require<VisionCoordinatePoint2D>("point"), "point");
        CoordinateValidation.RequireFrame(point.Frame, "point.frame");
        CoordinateValidation.RequireUnit(point.Unit, "point.unit");
        var transform = context.Require<VisionTransform2D>("transform");
        var det = CoordinateValidation.ValidateTransform(transform, "transform");
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("coordinate.transformCoordinatePoint transform is singular (determinant near zero).");
        CoordinateValidation.RequireFrameUnitMatch(point.Frame,point.Unit,transform.SourceFrame,transform.SourceUnit,"coordinate.transformCoordinatePoint");
        var q = CoordinateValidation.RequireFinitePoint(transform.Apply(new VisionPoint2D(point.X,point.Y)), "transformed point");
        CoordinateValidation.RequireMagnitude(q.X,"transformed point.x",1e12); CoordinateValidation.RequireMagnitude(q.Y,"transformed point.y",1e12);
        var transformedPoint = new VisionCoordinatePoint2D(q.X,q.Y,transform.TargetFrame,transform.TargetUnit);
        CoordinateValidation.RequireFrame(transformedPoint.Frame, "transformed.frame");
        CoordinateValidation.RequireUnit(transformedPoint.Unit, "transformed.unit");
        var diag = CoordinateValidation.Diagnostics(Type,
            ("inputX", point.X), ("inputY", point.Y), ("sourceFrame", point.Frame),
            ("outputX", transformedPoint.X), ("outputY", transformedPoint.Y), ("determinant", det),
            ("targetFrame", transformedPoint.Frame), ("unit", transformedPoint.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.CoordinatePoint(transformedPoint),
                ["x"] = VisionValue.Double(transformedPoint.X),
                ["y"] = VisionValue.Double(transformedPoint.Y),
                ["frame"] = VisionValue.String(transformedPoint.Frame),
                ["unit"] = VisionValue.String(transformedPoint.Unit),
                ["sourceFrame"] = VisionValue.String(point.Frame),
                ["determinant"] = VisionValue.Double(det),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["point"] = $"({transformedPoint.X:0.###},{transformedPoint.Y:0.###}) {transformedPoint.Unit}",
                ["frame"] = transformedPoint.Frame,
                ["sourceFrame"] = point.Frame,
                ["determinant"] = det,
                ["diagnostics"] = diag
            }));
    }
}
public sealed class TransformCoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var transform=context.Require<VisionTransform2D>("transform"); var det=CoordinateValidation.ValidateTransform(transform,"transform");
        CoordinateValidation.RequireFrameUnitMatch(pose.Frame,pose.Unit,transform.SourceFrame,transform.SourceUnit,"coordinate.transformCoordinatePose");
        var mapped=transform.Apply(new VisionPoint2D(pose.X,pose.Y));
        var q=CoordinateValidation.RequireFinitePose(new VisionCoordinatePose2D(mapped.X,mapped.Y,transform.ApplyAngleDeg(new VisionPoint2D(pose.X,pose.Y),pose.ThetaDeg),transform.TargetFrame,transform.TargetUnit),"transformed pose");
        var matrixType=CoordinateValidation.MatrixType(transform);
        var diag=CoordinateValidation.Diagnostics(Type,("poseFrame",pose.Frame),("transformSourceFrame",transform.SourceFrame),("poseUnit",pose.Unit),("transformSourceUnit",transform.SourceUnit),("inputThetaDeg",pose.ThetaDeg),("outputX",q.X),("outputY",q.Y),("outputThetaDeg",q.ThetaDeg),("determinant",det),("targetFrame",q.Frame),("unit",q.Unit),("orientationRule","Jacobian local-X-axis mapping"),("angleInterval","(-180,180]"),("transformType",matrixType),("orientationDistorted",matrixType!="rigid"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"pose",VisionValue.CoordinatePose(q)},{"x",VisionValue.Double(q.X)},{"y",VisionValue.Double(q.Y)},{"thetaDeg",VisionValue.Double(q.ThetaDeg)},{"frame",VisionValue.String(q.Frame)},{"unit",VisionValue.String(q.Unit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"pose",$"X={q.X:0.###}, Y={q.Y:0.###}, R={q.ThetaDeg:0.###}"},{"frame",q.Frame},{"unit",q.Unit},{"diagnostics",diag}}));
    }
}
public sealed class RobotGuidance2DNode : IVisionNodeExecutor
{
    public string Type => "robot.guidance2d";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var pose=CoordinateValidation.RequireFinitePose(context.Require<VisionCoordinatePose2D>("pose"),"pose");
        var expectedFrame=CoordinateValidation.RequireFrame(node.GetString("expectedFrame","RobotBase"),"expectedFrame");
        var configuredFrame=node.GetString("frame","").Trim();
        var outputFrame=configuredFrame.Length==0 ? expectedFrame : CoordinateValidation.RequireFrame(configuredFrame,"frame");
        if (!pose.Frame.Equals(expectedFrame, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Robot guidance expected pose in '{expectedFrame}', got '{pose.Frame}'. Resolve the frame transform first.");
        if (!outputFrame.Equals(expectedFrame, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Robot guidance output frame '{outputFrame}' does not match expectedFrame '{expectedFrame}'.");
        var offsetX=CoordinateValidation.RequireFinite(node.GetDouble("offsetX",0),"offsetX"); var offsetY=CoordinateValidation.RequireFinite(node.GetDouble("offsetY",0),"offsetY"); var angleOffset=CoordinateValidation.RequireFinite(node.GetDouble("angleOffsetDeg",0),"angleOffsetDeg");
        var offsetUnit=node.GetString("offsetUnit","").Trim();
        if (offsetUnit.Length>0 && !offsetUnit.Equals(pose.Unit,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"robot.guidance2d offsetUnit '{offsetUnit}' does not match pose unit '{pose.Unit}'.");
        var effectiveOffsetUnit=offsetUnit.Length==0?pose.Unit:offsetUnit;
        var robot=CoordinateValidation.RequireNonEmpty(node.GetString("robot","ABB"),"robot"); var mode=CoordinateValidation.RequireNonEmpty(node.GetString("guidanceMode","EyeToHand"),"guidanceMode");
        if (mode is not ("EyeToHand" or "EyeInHand" or "Manual")) throw new InvalidOperationException($"robot.guidance2d guidanceMode '{mode}' is unsupported; use EyeToHand, EyeInHand or Manual.");
        var poseSource=node.GetString("poseSource","").Trim();
        if (poseSource.Length==0) poseSource=mode; // legacy default: selected mode is the caller's source attestation
        if (poseSource is not ("EyeToHand" or "EyeInHand" or "Manual" or "RobotFrame"))
            throw new InvalidOperationException($"robot.guidance2d poseSource '{poseSource}' is unsupported.");
        if (!string.Equals(poseSource,mode,StringComparison.OrdinalIgnoreCase)
            && !(poseSource.Equals("RobotFrame",StringComparison.OrdinalIgnoreCase) && mode.Equals("Manual",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"robot.guidance2d pose source '{poseSource}' does not match guidanceMode '{mode}'; refusing coordinate-system mixing.");
        var rad=pose.ThetaDeg*Math.PI/180.0; var x=pose.X+Math.Cos(rad)*offsetX-Math.Sin(rad)*offsetY; var y=pose.Y+Math.Sin(rad)*offsetX+Math.Cos(rad)*offsetY;
        CoordinateValidation.RequireFinite(x,"guidance.x"); CoordinateValidation.RequireFinite(y,"guidance.y");
        var r=VisionTransform2D.NormalizeAngleDeg(pose.ThetaDeg+angleOffset); var offsetDistance=Math.Sqrt(offsetX*offsetX+offsetY*offsetY);
        var target=new VisionRobotTarget2D(x,y,r,outputFrame,pose.Unit,robot,mode);
        var diag=CoordinateValidation.Diagnostics(Type,("inputX",pose.X),("inputY",pose.Y),("inputRDeg",pose.ThetaDeg),("x",x),("y",y),("rDeg",r),("offsetDistance",offsetDistance),("offsetUnit",effectiveOffsetUnit),("rotationOrder","rotate-local-offset-by-input-angle-then-add; angleOffsetDeg-is-CCW-add"),("frame",outputFrame),("frameDefault",configuredFrame.Length==0?"expectedFrame":"configured"),("expectedFrame",expectedFrame),("poseSource",poseSource),("unit",pose.Unit),("robot",robot),("mode",mode));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"target",VisionValue.RobotTarget(target)},{"inputPose",VisionValue.CoordinatePose(pose)},{"inputX",VisionValue.Double(pose.X)},{"inputY",VisionValue.Double(pose.Y)},{"inputR",VisionValue.Double(pose.ThetaDeg)},{"x",VisionValue.Double(x)},{"y",VisionValue.Double(y)},{"r",VisionValue.Double(r)},{"frame",VisionValue.String(outputFrame)},{"unit",VisionValue.String(pose.Unit)},{"offsetUnit",VisionValue.String(effectiveOffsetUnit)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"robot",robot},{"mode",mode},{"poseSource",poseSource},{"inputPose",$"X={pose.X:0.###}, Y={pose.Y:0.###}, R={pose.ThetaDeg:0.###}"},{"target",$"X={x:0.###}, Y={y:0.###}, R={r:0.###}"},{"frame",outputFrame},{"unit",pose.Unit},{"offsetUnit",effectiveOffsetUnit},{"diagnostics",diag}}));
    }
}
public sealed class J4TcpCompensationNode : IVisionNodeExecutor
{
    public string Type => "robot.j4TcpCompensation";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

        cancellationToken.ThrowIfCancellationRequested();
        var target=context.Require<VisionRobotTarget2D>("target");
        CoordinateValidation.RequireFinite(target.X,"target.x"); CoordinateValidation.RequireFinite(target.Y,"target.y"); CoordinateValidation.RequireFinite(target.RDeg,"target.rDeg");
        CoordinateValidation.RequireFrame(target.Frame,"target.frame"); CoordinateValidation.RequireUnit(target.Unit,"target.unit");
        var offsetFrame=node.GetString("offsetFrame","").Trim();
        var offsetUnit=node.GetString("offsetUnit","").Trim();
        if (offsetFrame.Length>0 && !offsetFrame.Equals(target.Frame,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"robot.j4TcpCompensation offsetFrame '{offsetFrame}' does not match target frame '{target.Frame}'.");
        if (offsetUnit.Length>0 && !offsetUnit.Equals(target.Unit,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"robot.j4TcpCompensation offsetUnit '{offsetUnit}' does not match target unit '{target.Unit}'.");
        var tcpX=CoordinateValidation.RequireFinite(node.GetDouble("tcpOffsetX",0),"tcpOffsetX"); var tcpY=CoordinateValidation.RequireFinite(node.GetDouble("tcpOffsetY",0),"tcpOffsetY");
        var pivotX=CoordinateValidation.RequireFinite(node.GetDouble("j4PivotOffsetX",0),"j4PivotOffsetX"); var pivotY=CoordinateValidation.RequireFinite(node.GetDouble("j4PivotOffsetY",0),"j4PivotOffsetY");
        var zero=CoordinateValidation.RequireFinite(node.GetDouble("angleZeroOffsetDeg",0),"angleZeroOffsetDeg");
        var angleUnit = node.GetString("angleUnit", "deg").Trim().ToLowerInvariant();
        if (angleUnit is not ("deg" or "rad"))
            throw new InvalidOperationException("robot.j4TcpCompensation angleUnit must be deg or rad.");
        if (angleUnit == "rad")
        {
            // Target/zero angles arrive in radians; convert to degrees for NormalizeAngleDeg.
            target = target with { RDeg = target.RDeg * 180.0 / Math.PI };
            zero *= 180.0 / Math.PI;
        }
        var maxEccentricity=CoordinateValidation.RequireFinite(node.GetDouble("maxEccentricity",0),"maxEccentricity");
        if (maxEccentricity < 0) throw new InvalidOperationException("robot.j4TcpCompensation maxEccentricity must be >= 0 (0 disables).");
        var mode = node.GetString("compensationMode", "tcpToPivot").Trim().ToLowerInvariant();
        if (mode is not ("tcptopivot" or "pivottotcp"))
            throw new InvalidOperationException("robot.j4TcpCompensation compensationMode must be tcpToPivot or pivotToTcp.");
        var maxAngleDeg=CoordinateValidation.RequireFinite(node.GetDouble("maxAngleDeg",0),"maxAngleDeg");
        if (maxAngleDeg is < 0 or > 360) throw new InvalidOperationException("robot.j4TcpCompensation maxAngleDeg must be 0 (off) or 0..360.");
        var rawCommandR=CoordinateValidation.RequireFinite(target.RDeg+zero,"commandAngle");
        if (maxAngleDeg > 0 && Math.Abs(rawCommandR) > maxAngleDeg)
            throw new InvalidOperationException($"robot.j4TcpCompensation command angle {rawCommandR:0.###} deg exceeds maxAngleDeg {maxAngleDeg:0.###}.");
        var commandR=VisionTransform2D.NormalizeAngleDeg(rawCommandR); var ex=tcpX-pivotX; var ey=tcpY-pivotY; var ecc=Math.Sqrt(ex*ex+ey*ey);
        if (!double.IsFinite(ecc)) throw new InvalidOperationException("robot.j4TcpCompensation eccentricity must be finite.");
        if (maxEccentricity > 0 && ecc > maxEccentricity)
            throw new InvalidOperationException($"robot.j4TcpCompensation eccentricity {ecc:0.###} exceeds maxEccentricity {maxEccentricity:0.###}.");
        var rad=commandR*Math.PI/180.0; var rotatedX=Math.Cos(rad)*ex-Math.Sin(rad)*ey; var rotatedY=Math.Sin(rad)*ex+Math.Cos(rad)*ey;
        double commandX, commandY;
        if (mode == "tcptopivot")
        {
            commandX = CoordinateValidation.RequireFinite(target.X - rotatedX, "command.x");
            commandY = CoordinateValidation.RequireFinite(target.Y - rotatedY, "command.y");
        }
        else
        {
            commandX = CoordinateValidation.RequireFinite(target.X + rotatedX, "command.x");
            commandY = CoordinateValidation.RequireFinite(target.Y + rotatedY, "command.y");
        }
        var compensated=target with { X=commandX, Y=commandY, RDeg=commandR };
        var diag=CoordinateValidation.Diagnostics(Type,("desiredX",target.X),("desiredY",target.Y),("desiredRDeg",target.RDeg),("commandX",commandX),("commandY",commandY),("commandRDeg",commandR),("eccentricity",ecc),("eccentricityX",ex),("eccentricityY",ey),("rotatedEccentricityX",rotatedX),("rotatedEccentricityY",rotatedY),("rotationDirection","CCW-positive-degrees"),("angleZeroSign","added-before-CCW-rotation"),("formula",mode=="tcptopivot"?"command=desired-R(angle)*(tcp-pivot)":"command=desired+R(angle)*(tcp-pivot)"),("maxEccentricity",maxEccentricity),("maxAngleDeg",maxAngleDeg),("compensationMode",mode),("angleUnit",angleUnit),("offsetFrame",offsetFrame.Length==0?target.Frame:offsetFrame),("offsetUnit",offsetUnit.Length==0?target.Unit:offsetUnit),("frame",target.Frame),("unit",target.Unit));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string,VisionValue>{{"target",VisionValue.RobotTarget(compensated)},{"inputTarget",VisionValue.RobotTarget(target)},{"desiredX",VisionValue.Double(target.X)},{"desiredY",VisionValue.Double(target.Y)},{"desiredR",VisionValue.Double(target.RDeg)},{"x",VisionValue.Double(commandX)},{"y",VisionValue.Double(commandY)},{"r",VisionValue.Double(commandR)},{"eccentricity",VisionValue.Double(ecc)},{"eccentricityX",VisionValue.Double(ex)},{"eccentricityY",VisionValue.Double(ey)},{"rotationDirection",VisionValue.String("CCW-positive-degrees")},{"frame",VisionValue.String(target.Frame)},{"unit",VisionValue.String(target.Unit)},{"compensationMode",VisionValue.String(mode)},{"diagnostics",VisionValue.String(diag)}},new Dictionary<string,object?>{{"desiredTcp",$"X={target.X:0.###}, Y={target.Y:0.###}, R={target.RDeg:0.###}"},{"commandPivot",$"X={commandX:0.###}, Y={commandY:0.###}, R={commandR:0.###}"},{"eccentricity",$"({ex:0.###},{ey:0.###}) {target.Unit}"},{"rotationDirection","CCW-positive-degrees"},{"diagnostics",diag}}));
    }
}
