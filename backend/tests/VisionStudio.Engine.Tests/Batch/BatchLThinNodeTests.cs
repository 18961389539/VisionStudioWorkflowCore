using System.Text.Json;
using VisionStudio.Engine.Nodes;
namespace VisionStudio.Engine.Tests;
public sealed class BatchLThinNodeTests
{
    private static NodeDefinition Def(string type, string json="{}") => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string,VisionValue> inputs) => new(inputs);
    [Fact] public async Task TransformCoordinatePose_RotationAndMismatch()
    {
        var rot=VisionTransform2DMath.Rigid("Workpiece","RobotBase","mm",0,0,90);
        var q=await new TransformCoordinatePoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(new(10,0,0,"Workpiece","mm"))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformCoordinatePose"),CancellationToken.None);
        Assert.Equal(0d,(double)q.Outputs["x"].Value!,6); Assert.Equal(10d,(double)q.Outputs["y"].Value!,6); Assert.Equal(90d,(double)q.Outputs["thetaDeg"].Value!,6);
        var identity=VisionTransform2DMath.Rigid("Workpiece","Workpiece","mm",0,0,0);
        var same=await new TransformCoordinatePoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(new(1,2,3,"Workpiece","mm"))},{"transform",VisionValue.Transform(identity)}}),Def("coordinate.transformCoordinatePose"),CancellationToken.None);
        Assert.Equal(3d,(double)same.Outputs["thetaDeg"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new TransformCoordinatePoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(new(0,0,0,"Camera","mm"))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformCoordinatePose"),CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new TransformCoordinatePoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(new(double.NaN,0,0,"Workpiece","mm"))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformCoordinatePose"),CancellationToken.None));
    }
    [Fact] public async Task TransformPoseAndPoint_RotationCases()
    {
        var rot=VisionTransform2DMath.Rigid("ImagePixel","Workpiece","mm",100,0,90);
        var p=await new TransformPoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.Pose(new(10,0,0))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformPose"),CancellationToken.None);
        Assert.Equal(100d,(double)p.Outputs["x"].Value!,6); Assert.Equal(10d,(double)p.Outputs["y"].Value!,6);
        var p2=await new TransformPoseNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.Pose(new(0,0,45))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformPose"),CancellationToken.None);
        Assert.Equal(135d,(double)p2.Outputs["thetaDeg"].Value!,6);
        var pt=await new TransformPointNode().ExecuteAsync(Ctx(new(){{"point",VisionValue.Point(new(0,10))},{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformPoint"),CancellationToken.None);
        Assert.Equal(90d,(double)pt.Outputs["x"].Value!,6);
        var inv=await new InverseTransformNode().ExecuteAsync(Ctx(new(){{"transform",VisionValue.Transform(rot)}}),Def("coordinate.transformInverse"),CancellationToken.None);
        Assert.Contains("Determinant",(string)inv.Outputs["diagnostics"].Value!);
        var inv2=await new InverseTransformNode().ExecuteAsync(Ctx(new(){{"transform",VisionValue.Transform((VisionTransform2D)inv.Outputs["transform"].Value!)}}),Def("coordinate.transformInverse"),CancellationToken.None);
        Assert.Equal("ImagePixel",(string)inv2.Outputs["sourceFrame"].Value!);
    }
    [Fact] public async Task J4AndGuidance_AdditionalCases()
    {
        var target=new VisionRobotTarget2D(0,0,90,"RobotBase","mm","ABB","EyeToHand");
        var j=await new J4TcpCompensationNode().ExecuteAsync(Ctx(new(){{"target",VisionValue.RobotTarget(target)}}),Def("robot.j4TcpCompensation","{\"tcpOffsetX\":10,\"angleZeroOffsetDeg\":-90}"),CancellationToken.None);
        Assert.Equal(0d,(double)j.Outputs["r"].Value!,6);
        var j2=await new J4TcpCompensationNode().ExecuteAsync(Ctx(new(){{"target",VisionValue.RobotTarget(target with {RDeg=0})}}),Def("robot.j4TcpCompensation","{\"tcpOffsetX\":0,\"tcpOffsetY\":5,\"j4PivotOffsetY\":5}"),CancellationToken.None);
        Assert.Equal(0d,(double)j2.Outputs["eccentricity"].Value!,6);
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await new J4TcpCompensationNode().ExecuteAsync(Ctx(new(){{"target",VisionValue.RobotTarget(target)}}),Def("robot.j4TcpCompensation","{\"tcpOffsetX\":1e999}"),CancellationToken.None));
        var pose=new VisionCoordinatePose2D(0,0,0,"RobotBase","mm");
        var g=await new RobotGuidance2DNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("robot.guidance2d","{\"guidanceMode\":\"EyeInHand\",\"angleOffsetDeg\":45}"),CancellationToken.None);
        Assert.Equal(45d,(double)g.Outputs["r"].Value!,6);
        var g2=await new RobotGuidance2DNode().ExecuteAsync(Ctx(new(){{"pose",VisionValue.CoordinatePose(pose)}}),Def("robot.guidance2d","{\"guidanceMode\":\"Manual\"}"),CancellationToken.None);
        Assert.Equal("RobotBase",(string)g2.Outputs["frame"].Value!);
    }
}

