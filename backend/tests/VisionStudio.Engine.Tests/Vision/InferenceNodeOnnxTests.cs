using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class InferenceNodeOnnxTests
{
    [Fact]
    public async Task OrientedDetectionMapsRotationAndSuppressesOnlySameClassDuplicates()
    {
        var result = await Run(new OnnxOrientedDetectionNode(), "inference-obb.onnx");
        using var overlay = result.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.Equal(64, overlay.Width);
        Assert.Equal(32, overlay.Height);

        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Collection(detections,
            box => AssertOrientedBox(box, classId: 0, angle: 30),
            box => AssertOrientedBox(box, classId: 1, angle: -30));

        using var detail = JsonDocument.Parse(result.Outputs["result"].Require<string>("result"));
        var first = detail.RootElement.GetProperty("detections")[0];
        Assert.True(detail.RootElement.GetProperty("nmsApplied").GetBoolean());
        Assert.Equal(4, first.GetProperty("polygon").GetArrayLength());
        Assert.Equal(Math.PI / 6, first.GetProperty("angleRad").GetDouble(), 6);
    }

    [Fact]
    public async Task PosePairsKeypointsWithKeptDetectionAndDrawsOnlyVisibleSkeletonEdges()
    {
        var executor = new OnnxPoseNode();
        var withSkeleton = await Run(executor, "inference-pose.onnx", ("drawSkeleton", true));
        using var overlay = withSkeleton.Outputs["overlay"].Require<VisionImage>("overlay");
        var withoutSkeleton = await Run(executor, "inference-pose.onnx", ("drawSkeleton", false));
        using var pointsOnly = withoutSkeleton.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.Equal(1L, withSkeleton.Outputs["count"].Require<long>("count"));

        using var detail = JsonDocument.Parse(withSkeleton.Outputs["keypoints"].Require<string>("keypoints"));
        var pose = Assert.Single(detail.RootElement.EnumerateArray());
        var keypoints = pose.GetProperty("keypoints");
        Assert.Equal(0.95, pose.GetProperty("score").GetDouble(), 6);
        Assert.Equal(17, keypoints.GetArrayLength());
        AssertKeypoint(keypoints[5], 5, 20, 12, 0.9);
        AssertKeypoint(keypoints[6], 6, 44, 12, 0.8);
        AssertKeypoint(keypoints[7], 7, 20, 24, 0.2);

        Assert.NotEqual(default(Vec3b), overlay.Mat.At<Vec3b>(12, 32));
        Assert.Equal(default(Vec3b), pointsOnly.Mat.At<Vec3b>(12, 32));
        Assert.Equal(default(Vec3b), overlay.Mat.At<Vec3b>(24, 20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstanceSegmentationUsesKeptCoefficientsAndMapsMaskToSource(bool useNamedOutputs)
    {
        var result = await Run(new OnnxInstanceSegmentationNode(), "inference-instance-seg.onnx",
            ("outputName", useNamedOutputs ? "instances" : ""),
            ("protoOutputName", useNamedOutputs ? "prototypes" : ""),
            ("includeMaskRuns", true));
        using var mask = result.Outputs["mask"].Require<VisionImage>("mask");
        using var overlay = result.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.Equal((64, 32), (mask.Width, mask.Height));
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(1L, result.Outputs["maskClassOffset"].Require<long>("maskClassOffset"));
        Assert.Equal((byte)2, mask.Mat.At<byte>(8, 8));
        Assert.Equal((byte)2, mask.Mat.At<byte>(23, 31));
        Assert.Equal((byte)1, mask.Mat.At<byte>(8, 32));
        Assert.Equal((byte)1, mask.Mat.At<byte>(23, 55));
        Assert.Equal((byte)0, mask.Mat.At<byte>(7, 40));
        Assert.Equal((byte)0, mask.Mat.At<byte>(24, 40));
        Assert.Equal((byte)0, mask.Mat.At<byte>(16, 56));
        Assert.Equal(768, Cv2.CountNonZero(mask.Mat));

        // Overlay colours are keyed by class id (mask value minus maskClassOffset), sharing the
        // detection/pose palette: class 0 -> slot 1, class 1 -> slot 2.
        var class0Color = InferenceStats.SegmentationColorBgr(1);
        Assert.Equal(new Vec3b((byte)(class0Color.B / 2), (byte)(class0Color.G / 2), (byte)(class0Color.R / 2)),
            overlay.Mat.At<Vec3b>(8, 32));
        var class1Color = InferenceStats.SegmentationColorBgr(2);
        Assert.Equal(new Vec3b((byte)(class1Color.B / 2), (byte)(class1Color.G / 2), (byte)(class1Color.R / 2)),
            overlay.Mat.At<Vec3b>(8, 8));

        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(detections.Select(detection => detection.Index), contours.Select(contour => contour.Index));
        Assert.Collection(contours,
            contour => AssertInstanceContour(contour, x: 32),
            contour => AssertInstanceContour(contour, x: 8));

        using var detail = JsonDocument.Parse(result.Outputs["instances"].Require<VisionJson>("instances").Text);
        var root = detail.RootElement;
        Assert.Equal(mask.Width, root.GetProperty("maskWidth").GetInt32());
        Assert.Equal(mask.Height, root.GetProperty("maskHeight").GetInt32());
        Assert.Equal("row-major, alternating background/foreground runs", root.GetProperty("maskRunOrder").GetString());
        var instances = root.GetProperty("instances").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1 }, instances.Select(instance => instance.GetProperty("classId").GetInt32()));
        Assert.All(instances, instance =>
        {
            Assert.Equal(384, instance.GetProperty("area").GetInt32());
            Assert.False(instance.TryGetProperty("maskSize", out _));
            Assert.False(instance.TryGetProperty("maskRunOrder", out _));
            var runs = instance.GetProperty("maskRuns").EnumerateArray().Select(run => run.GetInt32()).ToArray();
            Assert.Equal(64 * 32, runs.Sum());
            Assert.Equal(384, runs.Where((_, index) => index % 2 == 1).Sum());
        });

        // result is a decode summary; the instance payload is emitted once, on the instances port.
        using var resultDoc = JsonDocument.Parse(result.Outputs["result"].Require<string>("result"));
        Assert.Equal(2, resultDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, resultDoc.RootElement.GetProperty("maskClassOffset").GetInt32());
        Assert.False(resultDoc.RootElement.TryGetProperty("instances", out _));
    }

    [Fact]
    public async Task InstanceSegmentationOmitsMaskRunsByDefault()
    {
        var result = await Run(new OnnxInstanceSegmentationNode(), "inference-instance-seg.onnx");

        using var detail = JsonDocument.Parse(result.Outputs["instances"].Require<VisionJson>("instances").Text);
        Assert.False(detail.RootElement.TryGetProperty("maskRunOrder", out _));
        var instances = detail.RootElement.GetProperty("instances").EnumerateArray().ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance => Assert.False(instance.TryGetProperty("maskRuns", out _)));
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, string model, params (string Name, object Value)[] overrides)
    {
        using var input = VisionImage.Own(new Mat(32, 64, MatType.CV_8UC3, Scalar.All(0)));
        var parameters = new Dictionary<string, JsonElement>
        {
            ["modelPath"] = JsonSerializer.SerializeToElement(Path.Combine(AppContext.BaseDirectory, "Fixtures", model)),
            ["inputWidth"] = JsonSerializer.SerializeToElement(32),
            ["inputHeight"] = JsonSerializer.SerializeToElement(32),
            ["letterbox"] = JsonSerializer.SerializeToElement(true),
            ["tensorOrder"] = JsonSerializer.SerializeToElement("channelsLast"),
        };
        foreach (var (name, value) in overrides)
            parameters[name] = JsonSerializer.SerializeToElement(value);
        var node = new NodeDefinition("inference", executor.Type, null, null, parameters);
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) });
        return await executor.ExecuteAsync(context, node, CancellationToken.None);
    }

    private static void AssertInstanceContour(VisionContour contour, double x)
    {
        Assert.Equal(-1, contour.ParentIndex);
        Assert.False(contour.IsHole);
        Assert.True(contour.Closed);
        Assert.NotEmpty(contour.Points);
        Assert.True(contour.Points.Count <= 2000);
        Assert.Equal(x, contour.BoundingBox.X, 6);
        Assert.Equal(8, contour.BoundingBox.Y, 6);
        Assert.Equal(24, contour.BoundingBox.Width, 6);
        Assert.Equal(16, contour.BoundingBox.Height, 6);
        Assert.Equal(345, contour.Area, 6);
        Assert.True(contour.Perimeter > 0);
    }

    private static void AssertOrientedBox(VisionDetection detection, int classId, double angle)
    {
        Assert.Equal(classId, detection.ClassId);
        Assert.Equal(32, detection.BoundingBox.X, 6);
        Assert.Equal(16, detection.BoundingBox.Y, 6);
        Assert.Equal(20, detection.BoundingBox.Width, 6);
        Assert.Equal(12, detection.BoundingBox.Height, 6);
        Assert.Equal(angle, detection.BoundingBox.AngleDeg, 4);
    }

    private static void AssertKeypoint(JsonElement point, int index, double x, double y, double confidence)
    {
        Assert.Equal(index, point.GetProperty("index").GetInt32());
        Assert.Equal(x, point.GetProperty("x").GetDouble(), 6);
        Assert.Equal(y, point.GetProperty("y").GetDouble(), 6);
        Assert.Equal(confidence, point.GetProperty("conf").GetDouble(), 6);
    }
}
