using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 单应配准与 PnP 位姿（2026-10-28）。两个节点都是投影几何，断言必须"能恢复已知变换"——
/// 只检查不抛异常的话，把 H 的列主序读错、把 rvec 当成旋转矩阵都能蒙混过关。
/// </summary>
public sealed class BatchMatchingGeometryTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Points(IEnumerable<VisionPoint2D> points) => VisionValue.PointList(points.ToArray());

    [Fact]
    public async Task Homography_RecoversKnownPerspective()
    {
        var source = new[]
        {
            new VisionPoint2D(0, 0), new VisionPoint2D(100, 0),
            new VisionPoint2D(100, 80), new VisionPoint2D(0, 80)
        };
        var target = new[]
        {
            new VisionPoint2D(5, 7), new VisionPoint2D(110, 3),
            new VisionPoint2D(118, 92), new VisionPoint2D(-4, 85)
        };

        var result = await new HomographyRegistrationNode().ExecuteAsync(
            Ctx(new() { ["sourcePoints"] = Points(source), ["targetPoints"] = Points(target) }),
            Def("match.homography", """{"method":"least-squares","minInliers":4}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!, (string)result.Outputs["diagnostics"].Value!);
        var transform = (VisionTransform2D)result.Outputs["transform"].Value!;
        for (var index = 0; index < source.Length; index++)
        {
            var mapped = transform.Apply(source[index]);
            Assert.InRange(mapped.X, target[index].X - 1e-3, target[index].X + 1e-3);
            Assert.InRange(mapped.Y, target[index].Y - 1e-3, target[index].Y + 1e-3);
        }
        Assert.InRange((double)result.Outputs["reprojRms"].Value!, 0, 1e-3);
        // 目标四边形比源大约 10%，局部尺度应落在 1.0..1.25 之间而不是 1。
        Assert.InRange((double)result.Outputs["scaleX"].Value!, 0.95, 1.3);
        Assert.True(Math.Abs((double)result.Outputs["perspectiveX"].Value!) > 1e-6, "a trapezoid must produce a non-zero perspective term");
    }

    [Fact]
    public async Task Homography_RansacRejectsOutlier()
    {
        var source = new List<VisionPoint2D>
        {
            new(0, 0), new(100, 0), new(100, 80), new(0, 80), new(50, 40)
        };
        var target = source.Select(point => new VisionPoint2D(point.X * 2 + 10, point.Y * 2 + 5)).ToList();
        target[4] = new VisionPoint2D(target[4].X + 60, target[4].Y - 45); // 明显外点

        var result = await new HomographyRegistrationNode().ExecuteAsync(
            Ctx(new() { ["sourcePoints"] = Points(source), ["targetPoints"] = Points(target) }),
            Def("match.homography", """{"method":"ransac","ransacThresholdPx":1.5,"minInliers":4}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!, (string)result.Outputs["diagnostics"].Value!);
        Assert.Equal(4L, (long)result.Outputs["inlierCount"].Value!);
        var transform = (VisionTransform2D)result.Outputs["transform"].Value!;
        var mapped = transform.Apply(source[0]);
        Assert.InRange(mapped.X, 9.9, 10.1);
        Assert.InRange(mapped.Y, 4.9, 5.1);
    }

    [Fact]
    public async Task Homography_RejectsTooFewOrMismatchedPoints()
    {
        var three = Points([new(0, 0), new(10, 0), new(0, 10)]);
        var tooFew = await new HomographyRegistrationNode().ExecuteAsync(
            Ctx(new() { ["sourcePoints"] = three, ["targetPoints"] = three }),
            Def("match.homography"), CancellationToken.None);
        Assert.False((bool)tooFew.Outputs["found"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new HomographyRegistrationNode().ExecuteAsync(
                Ctx(new()
                {
                    ["sourcePoints"] = Points([new(0, 0), new(10, 0), new(0, 10), new(10, 10)]),
                    ["targetPoints"] = Points([new(0, 0), new(10, 0), new(0, 10)])
                }),
                Def("match.homography"), CancellationToken.None));
    }

    [Fact]
    public async Task Pose3D_RecoversKnownCameraPose()
    {
        var objects = new[]
        {
            new Point3f(0, 0, 0), new Point3f(100, 0, 0), new Point3f(100, 60, 0),
            new Point3f(0, 60, 0), new Point3f(50, 30, 25), new Point3f(20, 10, -15)
        };
        double[] rvec = [0.45, -0.15, 0.08];
        double[] tvec = [12, -18, 520];
        var camera = new double[,] { { 800, 0, 320 }, { 0, 800, 240 }, { 0, 0, 1 } };
        Cv2.ProjectPoints(objects, rvec, tvec, camera, [], out var projected, out _, 0);

        var imagePoints = projected.Select(point => new VisionPoint2D(point.X, point.Y)).ToArray();
        var objectJson = JsonSerializer.Serialize(objects.Select(point => new[] { point.X, point.Y, point.Z }));

        var result = await new PnpPoseNode().ExecuteAsync(
            Ctx(new() { ["imagePoints"] = Points(imagePoints) }),
            Def("match.pose3d", JsonSerializer.Serialize(new
            {
                objectPointsJson = objectJson,
                cameraMatrixJson = "[800,0,320,0,800,240,0,0,1]",
                distortionJson = "[]",
                solver = "iterative",
                unit = "mm"
            })), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!, (string)result.Outputs["diagnostics"].Value!);
        Assert.InRange((double)result.Outputs["x"].Value!, 11.0, 13.0);
        Assert.InRange((double)result.Outputs["y"].Value!, -19.0, -17.0);
        Assert.InRange((double)result.Outputs["z"].Value!, 518.0, 522.0);
        Assert.InRange((double)result.Outputs["reprojectionRms"].Value!, 0, 0.05);
        var pose = (VisionTransform3D)result.Outputs["pose"].Value!;
        Assert.Equal(16, pose.Matrix.Length);
        Assert.Equal(1d, pose.Matrix[15], 9);
        Assert.Equal("mm", pose.Unit);
    }

    [Fact]
    public async Task Pose3D_RejectsMismatchedOrTooFewPoints()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PnpPoseNode().ExecuteAsync(
                Ctx(new()
                {
                    ["imagePoints"] = Points([new(0, 0), new(10, 0), new(10, 10), new(0, 10)])
                }),
                // 物体点只给 3 个，与 4 个图像点不匹配：必须报错而不是猜顺序。
                Def("match.pose3d", """{"objectPointsJson":"[[0,0,0],[10,0,0],[10,10,0]]","cameraMatrixJson":"[800,0,320,0,800,240,0,0,1]"}"""),
                CancellationToken.None));

        var tooFew = await new PnpPoseNode().ExecuteAsync(
            Ctx(new()
            {
                ["imagePoints"] = Points([new(0, 0), new(10, 0), new(10, 10)])
            }),
            Def("match.pose3d", """{"objectPointsJson":"[[0,0,0],[10,0,0],[10,10,0]]","cameraMatrixJson":"[800,0,320,0,800,240,0,0,1]"}"""),
            CancellationToken.None);
        Assert.False((bool)tooFew.Outputs["found"].Value!);
    }
}
