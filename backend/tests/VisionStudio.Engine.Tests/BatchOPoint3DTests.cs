using System.Text.Json;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchOPoint3DTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionTransform3D Identity(string source = "Workpiece", string target = "RobotBase", string unit = "mm")
        => new(source, target, unit,
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ]);

    private static VisionTransform3D Translate(double tx, double ty, double tz, string source = "Workpiece", string target = "RobotBase", string unit = "mm")
        => new(source, target, unit,
        [
            1, 0, 0, tx,
            0, 1, 0, ty,
            0, 0, 1, tz,
            0, 0, 0, 1
        ]);

    [Fact]
    public async Task Point3D_BuildsFromParamsAndRejectsNonFinite()
    {
        var ok = await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.point3D", """{"x":10,"y":20,"z":30,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal(10d, (double)ok.Outputs["x"].Value!, 6);
        Assert.Equal(20d, (double)ok.Outputs["y"].Value!, 6);
        Assert.Equal(30d, (double)ok.Outputs["z"].Value!, 6);
        Assert.Equal("Workpiece", ok.Outputs["frame"].Value);
        Assert.Contains("coordinate.point3D", (string)ok.Outputs["diagnostics"].Value!);

        var fromInputs = await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()
        {
            ["x"] = VisionValue.Double(1), ["y"] = VisionValue.Double(2), ["z"] = VisionValue.Double(3)
        }), Def("coordinate.point3D", """{"frame":"Camera","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal(1d, (double)fromInputs.Outputs["x"].Value!, 6);
        Assert.Equal("Camera", fromInputs.Outputs["frame"].Value);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new() { ["x"] = VisionValue.Double(double.NaN) }),
                Def("coordinate.point3D"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
                Def("coordinate.point3D", """{"frame":"","unit":"mm"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
                Def("coordinate.point3D", """{"frame":"Workpiece","unit":"inch"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task TransformPoint3D_IdentityTranslateAndMismatches()
    {
        var point = new VisionCoordinatePoint3D(10, 20, 30, "Workpiece", "mm");
        var identity = await new TransformCoordinatePoint3DNode().ExecuteAsync(Ctx(new()
        {
            ["point"] = VisionValue.CoordinatePoint3D(point),
            ["transform"] = VisionValue.Transform3D(Identity())
        }), Def("coordinate.transformPoint3D"), CancellationToken.None);
        Assert.Equal(10d, (double)identity.Outputs["x"].Value!, 6);
        Assert.Equal("RobotBase", identity.Outputs["frame"].Value);
        Assert.Contains("coordinate.transformPoint3D", (string)identity.Outputs["diagnostics"].Value!);

        var moved = await new TransformCoordinatePoint3DNode().ExecuteAsync(Ctx(new()
        {
            ["point"] = VisionValue.CoordinatePoint3D(point),
            ["transform"] = VisionValue.Transform3D(Translate(100, -5, 1))
        }), Def("coordinate.transformPoint3D"), CancellationToken.None);
        Assert.Equal(110d, (double)moved.Outputs["x"].Value!, 6);
        Assert.Equal(15d, (double)moved.Outputs["y"].Value!, 6);
        Assert.Equal(31d, (double)moved.Outputs["z"].Value!, 6);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new TransformCoordinatePoint3DNode().ExecuteAsync(Ctx(new()
            {
                ["point"] = VisionValue.CoordinatePoint3D(point with { Frame = "Camera" }),
                ["transform"] = VisionValue.Transform3D(Identity())
            }), Def("coordinate.transformPoint3D"), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new TransformCoordinatePoint3DNode().ExecuteAsync(Ctx(new()
            {
                ["point"] = VisionValue.CoordinatePoint3D(point with { Unit = "cm" }),
                ["transform"] = VisionValue.Transform3D(Identity())
            }), Def("coordinate.transformPoint3D"), CancellationToken.None));

        var badMatrix = Identity();
        badMatrix = badMatrix with { Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 2] };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new TransformCoordinatePoint3DNode().ExecuteAsync(Ctx(new()
            {
                ["point"] = VisionValue.CoordinatePoint3D(point),
                ["transform"] = VisionValue.Transform3D(badMatrix)
            }), Def("coordinate.transformPoint3D"), CancellationToken.None));
    }
}
