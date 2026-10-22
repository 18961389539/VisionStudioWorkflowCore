using System.Text.Json;
using VisionStudio.Engine.Nodes;
namespace VisionStudio.Engine.Tests;
public sealed class BatchGCoordinateGeometryTests
{
    private static NodeDefinition Def(string type, string json="{}") => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string,VisionValue> inputs) => new(inputs);
    private static VisionTransform2D Rigid() => VisionTransform2DMath.Rigid("ImagePixel","Workpiece","mm",10,20,0);
    [Fact] public async Task CoordinatePoint_EmitsScalarsAndDiagnostics()
    {
        var r=await new CoordinatePointNode().ExecuteAsync(Ctx(new()),Def("coordinate.point","{\"x\":3,\"y\":4,\"frame\":\"Workpiece\",\"unit\":\"mm\"}"),CancellationToken.None);
        Assert.Equal(3d,(double)r.Outputs["x"].Value!); Assert.Contains("coordinate.point",(string)r.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new CoordinatePointNode().ExecuteAsync(Ctx(new()),Def("coordinate.point","{\"frame\":\"\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new CoordinatePointNode().ExecuteAsync(Ctx(new()),Def("coordinate.point","{\"unit\":\"furlong\"}"),CancellationToken.None));
    }
    [Fact] public async Task CoordinatePose_AndPoseToTransform()
    {
        var r=await new CoordinatePoseNode().ExecuteAsync(Ctx(new()),Def("coordinate.pose","{\"x\":1,\"y\":2,\"thetaDeg\":370}"),CancellationToken.None);
        Assert.Equal(10d,(double)r.Outputs["thetaDeg"].Value!);
        var pose=new VisionCoordinatePose2D(5,6,30,"RobotBase","mm");
        var tr=await new PoseToTransformNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("coordinate.poseToTransform","{\"sourceFrame\":\"Tool\"}"),CancellationToken.None);
        Assert.Equal(5d,(double)tr.Outputs["x"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PoseToTransformNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("coordinate.poseToTransform","{\"sourceFrame\":\"RobotBase\"}"),CancellationToken.None));
    }
    [Fact] public async Task TransformConstantInverseCompose_RoundTrips()
    {
        var c=await new ConstantTransformNode().ExecuteAsync(Ctx(new()),Def("coordinate.transformConstant","{\"sourceFrame\":\"A\",\"targetFrame\":\"B\",\"unit\":\"mm\",\"x\":10,\"y\":0,\"angleDeg\":0}"),CancellationToken.None);
        var t=(VisionTransform2D)c.Outputs["transform"].Value!;
        var inv=await new InverseTransformNode().ExecuteAsync(Ctx(new(){{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformInverse"),CancellationToken.None);
        Assert.Equal("B",(string)inv.Outputs["sourceFrame"].Value!);
        var comp=await new ComposeTransformNode().ExecuteAsync(Ctx(new(){{"transformA",VisionValue.Transform(t)},{"transformB",VisionValue.Transform((VisionTransform2D)inv.Outputs["transform"].Value!)}}),Def("coordinate.transformCompose"),CancellationToken.None);
        Assert.Equal(1d,(double)comp.Outputs["determinant"].Value!,6);
        var singular=new VisionTransform2D("A","B","mm",0,0,0,0,0,0,0,0,0,"mm");
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new InverseTransformNode().ExecuteAsync(Ctx(new(){{"transform",VisionValue.Transform(singular)}}),Def("coordinate.transformInverse"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ConstantTransformNode().ExecuteAsync(Ctx(new()),Def("coordinate.transformConstant","{\"unit\":\"yard\"}"),CancellationToken.None));
    }
    [Fact] public async Task TransformPointLinePose_ValidateAndEmit()
    {
        var t=Rigid();
        var pt=await new TransformPointNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(1,2))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformPoint"),CancellationToken.None);
        Assert.Equal(11d,(double)pt.Outputs["x"].Value!);
        var ln=await new TransformLineNode().ExecuteAsync(Ctx(new(){{"line",VisionValue.Line(new(new(0,0),new(3,4)))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformLine"),CancellationToken.None);
        Assert.Equal(5d,(double)ln.Outputs["lengthValue"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new TransformLineNode().ExecuteAsync(Ctx(new(){{"line",VisionValue.Line(new(new(1,1),new(1,1)))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformLine"),CancellationToken.None));
        var ps=await new TransformPoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.Pose(new(1,2,15))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformPose"),CancellationToken.None);
        Assert.Equal(11d,(double)ps.Outputs["x"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new TransformPointNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(double.NaN,0))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformPoint"),CancellationToken.None));
    }
    [Fact] public async Task CoordinateDistanceAndCoordinateTransforms_CheckFrames()
    {
        var a=new VisionCoordinatePoint2D(0,0,"Workpiece","mm"); var b=new VisionCoordinatePoint2D(3,4,"Workpiece","mm");
        var d=await new CoordinateDistanceNode().ExecuteAsync(Ctx(new(){{"pointA",VisionValue.CoordinatePoint(a)},{"pointB",VisionValue.CoordinatePoint(b)}}),Def("coordinate.distance"),CancellationToken.None);
        Assert.Equal(5d,(double)d.Outputs["distance"].Value!); Assert.Equal(3d,(double)d.Outputs["dx"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new CoordinateDistanceNode().ExecuteAsync(Ctx(new(){{"pointA",VisionValue.CoordinatePoint(a)},{"pointB",VisionValue.CoordinatePoint(b with {Frame="Other"})}}),Def("coordinate.distance"),CancellationToken.None));
        var t=VisionTransform2DMath.Rigid("Workpiece","RobotBase","mm",100,0,0);
        var q=await new TransformCoordinatePointNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.CoordinatePoint(a)},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformCoordinatePoint"),CancellationToken.None);
        Assert.Equal(100d,(double)q.Outputs["x"].Value!);
        var qp=await new TransformCoordinatePoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(new(1,2,10,"Workpiece","mm"))},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformCoordinatePose"),CancellationToken.None);
        Assert.Equal(101d,(double)qp.Outputs["x"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new TransformCoordinatePointNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.CoordinatePoint(a with {Unit="cm"})},{"transform",VisionValue.Transform(t)}}),Def("coordinate.transformCoordinatePoint"),CancellationToken.None));
    }
    [Fact] public async Task FrameTreeResolve_WorksAndRejectsDuplicates()
    {
        var json="{\"transformsJson\":\"[{\\\"sourceFrame\\\":\\\"Workpiece\\\",\\\"targetFrame\\\":\\\"Fixture\\\",\\\"unit\\\":\\\"mm\\\",\\\"x\\\":100,\\\"y\\\":0,\\\"angleDeg\\\":0},{\\\"sourceFrame\\\":\\\"Fixture\\\",\\\"targetFrame\\\":\\\"RobotBase\\\",\\\"unit\\\":\\\"mm\\\",\\\"x\\\":50,\\\"y\\\":0,\\\"angleDeg\\\":0}]\"}";
        var treeResult=await new FrameTreeNode().ExecuteAsync(Ctx(new()),Def("coordinate.frameTree",json),CancellationToken.None);
        Assert.Equal(2L,(long)treeResult.Outputs["transformCount"].Value!);
        var tree=(VisionFrameTree2D)treeResult.Outputs["tree"].Value!;
        var resolved=await new ResolveFrameTransformNode().ExecuteAsync(Ctx(new(){{"tree",VisionValue.FrameTree(tree)}}),Def("coordinate.frameResolve","{\"sourceFrame\":\"Workpiece\",\"targetFrame\":\"RobotBase\"}"),CancellationToken.None);
        Assert.Equal(2L,(long)resolved.Outputs["hopCount"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new FrameTreeNode().ExecuteAsync(Ctx(new()),Def("coordinate.frameTree","{\"transformsJson\":\"[]\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new ResolveFrameTransformNode().ExecuteAsync(Ctx(new(){{"tree",VisionValue.FrameTree(tree)}}),Def("coordinate.frameResolve","{\"sourceFrame\":\"Workpiece\",\"targetFrame\":\"Nowhere\"}"),CancellationToken.None));
    }
    [Fact] public async Task GeometryNodes_IntersectDistanceAngle()
    {
        var h=new VisionLine2D(new(0,0),new(10,0)); var v=new VisionLine2D(new(5,-5),new(5,5));
        var hit=await new IntersectionNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(h)},{"lineB",VisionValue.Line(v)}}),Def("geometry.intersection"),CancellationToken.None);
        Assert.Equal(5d,(double)hit.Outputs["x"].Value!); Assert.True((bool)hit.Outputs["withinSegments"].Value!);
        var outside=await new IntersectionNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(h)},{"lineB",VisionValue.Line(new(new(20,-5),new(20,5)))}}),Def("geometry.intersection"),CancellationToken.None);
        Assert.False((bool)outside.Outputs["withinSegments"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new IntersectionNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(h)},{"lineB",VisionValue.Line(new(new(20,-5),new(20,5)))}}),Def("geometry.intersection","{\"requireWithinSegments\":true}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new IntersectionNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(h)},{"lineB",VisionValue.Line(new(new(0,1),new(10,1)))}}),Def("geometry.intersection"),CancellationToken.None));
        var d=await new DistanceNode().ExecuteAsync(Ctx(new(){{"pointA",VisionValue.Point(new(0,0))},{"pointB",VisionValue.Point(new(3,4))}}),Def("geometry.distance"),CancellationToken.None);
        Assert.Equal(5d,(double)d.Outputs["distance"].Value!); Assert.Equal(1.5d,(double)d.Outputs["midX"].Value!);
        var ang=await new AngleNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(h)},{"lineB",VisionValue.Line(v)}}),Def("geometry.angle"),CancellationToken.None);
        Assert.Equal(90d,(double)ang.Outputs["angle"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new AngleNode().ExecuteAsync(Ctx(new(){{"lineA",VisionValue.Line(new(new(1,1),new(1,1)))},{"lineB",VisionValue.Line(v)}}),Def("geometry.angle"),CancellationToken.None));
    }
    [Fact] public async Task RobotGuidanceAndJ4_Validate()
    {
        var pose=new VisionCoordinatePose2D(100,200,90,"RobotBase","mm");
        var g=await new RobotGuidance2DNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("robot.guidance2d","{\"offsetX\":10,\"offsetY\":0}"),CancellationToken.None);
        Assert.Equal(100d,(double)g.Outputs["x"].Value!,6); Assert.Equal(210d,(double)g.Outputs["y"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RobotGuidance2DNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose with {Frame="Camera"})}}),Def("robot.guidance2d"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new RobotGuidance2DNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("robot.guidance2d","{\"guidanceMode\":\"Teleport\"}"),CancellationToken.None));
        var target=new VisionRobotTarget2D(100,100,0,"RobotBase","mm","ABB","EyeToHand");
        var j=await new J4TcpCompensationNode().ExecuteAsync(Ctx(new(){{"target",VisionValue.RobotTarget(target)}}),Def("robot.j4TcpCompensation","{\"tcpOffsetX\":10,\"tcpOffsetY\":0}"),CancellationToken.None);
        Assert.Equal(90d,(double)j.Outputs["x"].Value!,6); Assert.Equal(10d,(double)j.Outputs["eccentricity"].Value!,6);
    }
    [Fact] public async Task PlanarCalibration_EmitsPointCountAndRejectsBadJson()
    {
        var pairs="{\"pairsJson\":\"[{\\\"imageX\\\":0,\\\"imageY\\\":0,\\\"worldX\\\":0,\\\"worldY\\\":0},{\\\"imageX\\\":100,\\\"imageY\\\":0,\\\"worldX\\\":10,\\\"worldY\\\":0},{\\\"imageX\\\":0,\\\"imageY\\\":100,\\\"worldX\\\":0,\\\"worldY\\\":10},{\\\"imageX\\\":100,\\\"imageY\\\":100,\\\"worldX\\\":10,\\\"worldY\\\":10}]\"}";
        var r=await new PlanarCalibrationNode().ExecuteAsync(Ctx(new()),Def("calibration.planar",pairs),CancellationToken.None);
        Assert.Equal(4L,(long)r.Outputs["pointCount"].Value!); Assert.True((double)r.Outputs["rmse"].Value! < 1e-6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarCalibrationNode().ExecuteAsync(Ctx(new()),Def("calibration.planar","{\"pairsJson\":\"bad\"}"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new PlanarCalibrationNode().ExecuteAsync(Ctx(new()),Def("calibration.planar","{\"pairsJson\":\"[]\",\"targetUnit\":\"yard\"}"),CancellationToken.None));
    }
}

