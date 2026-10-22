using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class DualCameraExtrinsicsTests
{
    [Fact]
    public async Task RecoversKnownRelativePoseFromSyntheticPosePairs()
    {
        var expected = MakeRelative(tx: 120, ty: -35, tz: 8, yawDeg: 7);
        var pairs = new List<object>();
        for (var i = 0; i < 6; i++)
        {
            var targetToA = MakeRelative(tx: 40 + i * 12, ty: -20 + i * 5, tz: 400 + i * 8, yawDeg: -10 + i * 4, pitchDeg: 3 + i, rollDeg: i * 2);
            var targetToB = Multiply(expected, targetToA);
            pairs.Add(new { targetToCameraA = targetToA, targetToCameraB = targetToB });
        }

        var result = await Run(new
        {
            pairsJson = JsonSerializer.Serialize(new { pairs }),
            inputMode = "poses",
            method = "average",
            sourceFrame = "CameraA",
            targetFrame = "CameraB",
            unit = "mm"
        });

        var transform = result.Outputs["transform"].Require<VisionTransform3D>("transform");
        Assert.Equal("CameraA", transform.SourceFrame);
        Assert.Equal("CameraB", transform.TargetFrame);
        Assert.Equal("mm", transform.Unit);
        AssertNear(expected, transform.Matrix, translationTol: 1e-6, rotationTolDeg: 1e-4);
        Assert.InRange(result.Outputs["baseline"].Require<double>("baseline"), 120 - 1e-6, Math.Sqrt(120 * 120 + 35 * 35 + 8 * 8) + 1e-6);
        Assert.True(result.Outputs["translationRms"].Require<double>("translationRms") < 1e-6);
        Assert.True(result.Outputs["rotationRmsDeg"].Require<double>("rotationRmsDeg") < 1e-4);
        Assert.Equal(6L, result.Outputs["pairCount"].Require<long>("pairCount"));
        Assert.Contains("matrix", result.Outputs["matrixJson"].Require<string>("matrixJson"), StringComparison.Ordinal);
        Assert.Equal("poses-average", result.Summary["solvePath"]);
    }

    [Fact]
    public async Task PoseModeUsesAverageWhenAPointOnlyMethodIsStored()
    {
        var expected = MakeRelative(tx: 25, ty: 4, tz: 0, yawDeg: 2);
        var pairs = new List<object>();
        for (var i = 0; i < 4; i++)
        {
            var targetToA = MakeRelative(tx: i * 9, ty: 2, tz: 300, yawDeg: i * 2);
            pairs.Add(new { targetToCameraA = targetToA, targetToCameraB = Multiply(expected, targetToA) });
        }

        var result = await Run(new
        {
            pairsJson = JsonSerializer.Serialize(new { pairs }),
            inputMode = "poses",
            method = "stereoCalibrate"
        });

        Assert.Equal("poses-average", result.Summary["solvePath"]);
        Assert.Equal("average", result.Summary["method"]);
    }

    [Fact]
    public async Task PointsAverageRecoversRelativePoseViaPnP()
    {
        var expected = MakeRelative(tx: 90, ty: 15, tz: -5, yawDeg: -4);
        var cameraA = new double[] { 800, 0, 320, 0, 800, 240, 0, 0, 1 };
        var cameraB = new double[] { 810, 0, 330, 0, 805, 250, 0, 0, 1 };
        var objects = BoardObjectPoints(columns: 5, rows: 4, spacing: 20);
        var pairs = new List<object>();
        for (var i = 0; i < 5; i++)
        {
            var targetToA = MakeRelative(tx: -30 + i * 10, ty: 20 - i * 4, tz: 500 + i * 15, yawDeg: -8 + i * 3, pitchDeg: 2, rollDeg: 1);
            var targetToB = Multiply(expected, targetToA);
            pairs.Add(new
            {
                objectPoints = objects,
                imagePointsA = Project(objects, targetToA, cameraA),
                imagePointsB = Project(objects, targetToB, cameraB)
            });
        }

        var result = await Run(new
        {
            pairsJson = JsonSerializer.Serialize(new
            {
                pairs,
                cameraMatrixA = cameraA,
                cameraMatrixB = cameraB,
                distCoeffsA = new double[] { 0, 0, 0, 0, 0 },
                distCoeffsB = new double[] { 0, 0, 0, 0, 0 },
                imageWidth = 640,
                imageHeight = 480
            }),
            inputMode = "points",
            method = "average",
            unit = "mm"
        });

        var matrix = result.Outputs["transform"].Require<VisionTransform3D>("transform").Matrix;
        AssertNear(expected, matrix, translationTol: 0.05, rotationTolDeg: 0.05);
        Assert.Equal("points-pnp-average", result.Summary["solvePath"]);
    }

    [Fact]
    public async Task StereoCalibratePathProducesFiniteBaseline()
    {
        var expected = MakeRelative(tx: 100, ty: 0, tz: 0, yawDeg: 0);
        var camera = new double[] { 700, 0, 320, 0, 700, 240, 0, 0, 1 };
        var objects = BoardObjectPoints(columns: 6, rows: 5, spacing: 15);
        var pairs = new List<object>();
        for (var i = 0; i < 4; i++)
        {
            var targetToA = MakeRelative(tx: -40 + i * 15, ty: -10 + i * 3, tz: 450 + i * 20, yawDeg: -6 + i * 2, pitchDeg: 1 + i * 0.5, rollDeg: 0.5);
            var targetToB = Multiply(expected, targetToA);
            pairs.Add(new
            {
                objectPoints = objects,
                imagePointsA = Project(objects, targetToA, camera),
                imagePointsB = Project(objects, targetToB, camera)
            });
        }

        var result = await Run(new
        {
            pairsJson = JsonSerializer.Serialize(new
            {
                pairs,
                cameraMatrixA = camera,
                cameraMatrixB = camera,
                distCoeffsA = new double[] { 0, 0, 0, 0, 0 },
                distCoeffsB = new double[] { 0, 0, 0, 0, 0 },
                imageWidth = 640,
                imageHeight = 480
            }),
            inputMode = "points",
            method = "stereoCalibrate",
            imageWidth = 640,
            imageHeight = 480,
            unit = "mm"
        });

        Assert.Equal("stereoCalibrate", result.Summary["solvePath"]);
        var baseline = result.Outputs["baseline"].Require<double>("baseline");
        Assert.InRange(baseline, 95, 105);
        var tx = result.Outputs["transform"].Require<VisionTransform3D>("transform").Matrix[3];
        Assert.InRange(tx, 95, 105);
    }

    [Fact]
    public async Task ResidualGateRejectsOutlierPair()
    {
        var expected = MakeRelative(tx: 50, ty: 0, tz: 0, yawDeg: 0);
        var pairs = new List<object>();
        for (var i = 0; i < 4; i++)
        {
            var targetToA = MakeRelative(tx: i * 10, ty: 0, tz: 300, yawDeg: i);
            pairs.Add(new { targetToCameraA = targetToA, targetToCameraB = Multiply(expected, targetToA) });
        }
        // Corrupt last pair with a large wrong relative.
        var badA = MakeRelative(tx: 40, ty: 0, tz: 300, yawDeg: 4);
        var badRel = MakeRelative(tx: 50, ty: 40, tz: 0, yawDeg: 15);
        pairs.Add(new { targetToCameraA = badA, targetToCameraB = Multiply(badRel, badA) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new
        {
            pairsJson = JsonSerializer.Serialize(new { pairs }),
            inputMode = "poses",
            maxTranslationError = 5.0,
            unit = "mm"
        }));
        Assert.Contains("maxTranslationError", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CatalogRegistersDualCameraNode()
    {
        var item = BuiltInNodeCatalog.Require("calibration.dualCameraExtrinsics");
        Assert.Equal("Calibration", item.Category);
        Assert.Contains(item.Outputs, o => o.Name == "transform");
        Assert.Contains(item.Outputs, o => o.Name == "matrixJson");
        Assert.Contains(item.Parameters, p => p.Name == "inputMode");
        Assert.Contains(item.Parameters, p => p.Name == "method");
    }

    [Fact]
    public async Task TransformOutputMapsPointFromCameraAToCameraB()
    {
        var expected = MakeRelative(tx: 10, ty: 20, tz: 30, yawDeg: 0);
        var pairs = Enumerable.Range(0, 3).Select(i =>
        {
            var a = MakeRelative(tx: i * 5, ty: 1, tz: 200 + i, yawDeg: i);
            return new { targetToCameraA = a, targetToCameraB = Multiply(expected, a) };
        }).ToArray();
        var calib = await Run(new
        {
            pairsJson = JsonSerializer.Serialize(new { pairs }),
            inputMode = "poses",
            sourceFrame = "CameraA",
            targetFrame = "CameraB",
            unit = "mm"
        });
        var transform = calib.Outputs["transform"].Require<VisionTransform3D>("transform");
        var point = new VisionCoordinatePoint3D(1, 2, 3, "CameraA", "mm");
        var mapped = await new TransformCoordinatePoint3DNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.CoordinatePoint3D(point),
                ["transform"] = VisionValue.Transform3D(transform)
            }),
            new NodeDefinition("xf", "coordinate.transformPoint3D", null, null, null),
            CancellationToken.None);
        Assert.InRange(mapped.Outputs["x"].Require<double>("x"), 11 - 1e-6, 11 + 1e-6);
        Assert.InRange(mapped.Outputs["y"].Require<double>("y"), 22 - 1e-6, 22 + 1e-6);
        Assert.InRange(mapped.Outputs["z"].Require<double>("z"), 33 - 1e-6, 33 + 1e-6);
        Assert.Equal("CameraB", mapped.Outputs["frame"].Require<string>("frame"));
    }

    private static async Task<NodeExecutorResult> Run(object parameters)
    {
        var json = JsonSerializer.Serialize(parameters);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var node = new NodeDefinition("dual", "calibration.dualCameraExtrinsics", null, null, dict);
        return await new DualCameraExtrinsicsNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);
    }

    private static void AssertNear(double[] expected, double[] actual, double translationTol, double rotationTolDeg)
    {
        Assert.Equal(16, actual.Length);
        for (var i = 0; i < 3; i++)
            Assert.InRange(actual[i * 4 + 3], expected[i * 4 + 3] - translationTol, expected[i * 4 + 3] + translationTol);
        // Rotation: compare angle of relative
        var rel = Multiply(Inverse(expected), actual);
        var angle = Math.Acos(Math.Clamp((rel[0] + rel[5] + rel[10] - 1) / 2, -1, 1)) * 180 / Math.PI;
        Assert.True(angle <= rotationTolDeg, $"rotation error {angle} deg exceeds {rotationTolDeg}");
    }

    private static List<double[]> BoardObjectPoints(int columns, int rows, double spacing)
    {
        var points = new List<double[]>();
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < columns; c++)
            points.Add([c * spacing, r * spacing, 0]);
        return points;
    }

    private static List<double[]> Project(List<double[]> objects, double[] targetToCamera, double[] cameraMatrix)
    {
        var points = new List<double[]>(objects.Count);
        foreach (var p in objects)
        {
            var x = targetToCamera[0] * p[0] + targetToCamera[1] * p[1] + targetToCamera[2] * p[2] + targetToCamera[3];
            var y = targetToCamera[4] * p[0] + targetToCamera[5] * p[1] + targetToCamera[6] * p[2] + targetToCamera[7];
            var z = targetToCamera[8] * p[0] + targetToCamera[9] * p[1] + targetToCamera[10] * p[2] + targetToCamera[11];
            Assert.True(z > 1e-3, "synthetic point behind camera");
            var u = cameraMatrix[0] * x / z + cameraMatrix[2];
            var v = cameraMatrix[4] * y / z + cameraMatrix[5];
            points.Add([u, v]);
        }
        return points;
    }

    private static double[] MakeRelative(double tx, double ty, double tz, double yawDeg, double pitchDeg = 0, double rollDeg = 0)
    {
        var yaw = yawDeg * Math.PI / 180;
        var pitch = pitchDeg * Math.PI / 180;
        var roll = rollDeg * Math.PI / 180;
        var cy = Math.Cos(yaw); var sy = Math.Sin(yaw);
        var cp = Math.Cos(pitch); var sp = Math.Sin(pitch);
        var cr = Math.Cos(roll); var sr = Math.Sin(roll);
        // R = Rz(yaw) * Ry(pitch) * Rx(roll)
        var r00 = cy * cp; var r01 = cy * sp * sr - sy * cr; var r02 = cy * sp * cr + sy * sr;
        var r10 = sy * cp; var r11 = sy * sp * sr + cy * cr; var r12 = sy * sp * cr - cy * sr;
        var r20 = -sp; var r21 = cp * sr; var r22 = cp * cr;
        return [r00, r01, r02, tx, r10, r11, r12, ty, r20, r21, r22, tz, 0, 0, 0, 1];
    }

    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    private static double[] Inverse(double[] t)
    {
        var result = Identity();
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) result[r * 4 + c] = t[c * 4 + r];
        for (var r = 0; r < 3; r++) result[r * 4 + 3] = -(result[r * 4] * t[3] + result[r * 4 + 1] * t[7] + result[r * 4 + 2] * t[11]);
        return result;
    }
    private static double[] Multiply(double[] a, double[] b)
    {
        var result = Identity();
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++) result[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c];
            result[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] + a[r * 4 + 2] * b[11] + a[r * 4 + 3];
        }
        return result;
    }
}