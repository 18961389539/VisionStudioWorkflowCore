using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class HandEye3DNodeTests
{
    [Fact]
    public async Task HandEye3DRecoversTransformAndReportsCrossCheck()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var json = BuildConsistentSamples(truth, 10, out _);
        var result = await Solve(json, new { method = "DANIILIDIS", crossCheckMethod = "TSAI" });
        var model = JsonSerializer.Deserialize<HandEye3DCalibrationModel>(result.Outputs["calibrationJson"].Require<string>("calibrationJson"))!;
        Assert.Equal("eyeInHand", model.Mode);
        Assert.Equal("DANIILIDIS", model.Method);
        for (var i = 0; i < 16; i++) Assert.Equal(truth[i], model.Matrix[i], 2);
        Assert.Equal(10L, result.Outputs["usedSampleCount"].Require<long>("usedSampleCount"));
        Assert.Equal("[]", result.Outputs["rejectedSampleIndicesJson"].Require<string>("rejectedSampleIndicesJson"));
        Assert.False(result.Outputs.ContainsKey("outlierSampleIndex"));
        Assert.InRange(result.Outputs["crossCheckTranslationDelta"].Require<double>("crossCheckTranslationDelta"), 0, 0.05);
        Assert.InRange(result.Outputs["crossCheckAngleDeltaDeg"].Require<double>("crossCheckAngleDeltaDeg"), 0, 0.05);
        Assert.InRange(result.Outputs["translationRms"].Require<double>("translationRms"), 0, 0.01);
    }

    [Fact]
    public async Task HandEye3DIterativeRejectionRemovesBadPair()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var samples = BuildConsistentSampleList(truth, 10);
        samples[4].TargetToCamera[3] += 15;
        var json = JsonSerializer.Serialize(new { samples });

        var plain = await Solve(json, new { method = "DANIILIDIS", crossCheckMethod = "NONE" });
        Assert.Equal(10L, plain.Outputs["usedSampleCount"].Require<long>("usedSampleCount"));
        Assert.Equal("[]", plain.Outputs["rejectedSampleIndicesJson"].Require<string>("rejectedSampleIndicesJson"));
        var plainModel = JsonSerializer.Deserialize<HandEye3DCalibrationModel>(plain.Outputs["calibrationJson"].Require<string>("calibrationJson"))!;
        var plainError = Math.Abs(plainModel.Matrix[3] - truth[3]);

        var robust = await Solve(json, new { method = "DANIILIDIS", crossCheckMethod = "NONE", outlierRejection = "iterative" });
        Assert.Equal(9L, robust.Outputs["usedSampleCount"].Require<long>("usedSampleCount"));
        Assert.Contains("4", robust.Outputs["rejectedSampleIndicesJson"].Require<string>("rejectedSampleIndicesJson"));
        var model = JsonSerializer.Deserialize<HandEye3DCalibrationModel>(robust.Outputs["calibrationJson"].Require<string>("calibrationJson"))!;
        Assert.Equal(truth[3], model.Matrix[3], 1);
        Assert.Equal(truth[7], model.Matrix[7], 1);
        Assert.Equal(truth[11], model.Matrix[11], 1);
        Assert.InRange(robust.Outputs["translationRms"].Require<double>("translationRms"), 0, 0.01);
        Assert.True(Math.Abs(model.Matrix[3] - truth[3]) < plainError, "robust rejection should recover the truth better than the contaminated plain fit");
    }

    [Fact]
    public async Task HandEye3DMinTranslationSpanGateAndCrossCheckSkip()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var json = BuildConsistentSamples(truth, 8, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Solve(json, new { minTranslationSpan = 1e6 }));
        var sameMethod = await Solve(json, new { method = "TSAI", crossCheckMethod = "TSAI" });
        Assert.False(sameMethod.Outputs.ContainsKey("crossCheckTranslationDelta"));
    }

    [Fact]
    public async Task HandEye3DConnectedSamplesOverrideParametersAndAssetPinMustBePaired()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var json = BuildConsistentSamples(truth, 8, out _);
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue> { ["samplesJson"] = VisionValue.String(json) });
        var result = await new HandEye3DCalibrationNode().ExecuteAsync(context,
            Def("calibration.handEye3D", JsonSerializer.Serialize(new { samplesJson = "{}" })), CancellationToken.None);
        Assert.True(result.Outputs.ContainsKey("transform"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Solve(json, new { handEye3DAssetId = "he3d-a" }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Solve(json, new { handEye3DAssetVersion = 2 }));
    }

    private static async Task<NodeExecutorResult> Solve(string samplesJson, object extra)
    {
        var parameters = new Dictionary<string, object?> { ["samplesJson"] = samplesJson, ["mode"] = "eyeInHand" };
        foreach (var property in extra.GetType().GetProperties())
            parameters[property.Name] = property.GetValue(extra);
        return await new HandEye3DCalibrationNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Def("calibration.handEye3D", JsonSerializer.Serialize(parameters)), CancellationToken.None);
    }

    private static NodeDefinition Def(string type, string json)
        => new("n", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static string BuildConsistentSamples(double[] truth, int count, out List<HandEye3DSample> samples)
    {
        samples = BuildConsistentSampleList(truth, count);
        return JsonSerializer.Serialize(new { samples });
    }

    private static List<HandEye3DSample> BuildConsistentSampleList(double[] truth, int count)
    {
        var target = Rigid(0.3, 0.1, -0.2, 210, 45, -30);
        var samples = new List<HandEye3DSample>();
        for (var i = 0; i < count; i++)
        {
            var robot = Rigid(0.12 * i, 0.08 * (i % 3) - 0.1, 0.05 * i + 0.03 * (i % 2), 12 * i, 6 * (i % 4), 3 * i);
            var vision = Multiply(Multiply(Inverse(truth), Inverse(robot)), target);
            samples.Add(new HandEye3DSample(robot, vision));
        }
        return samples;
    }

    private static double[] Rigid(double ax, double ay, double az, double tx, double ty, double tz)
    {
        using var rv = new Mat(3, 1, MatType.CV_64F);
        rv.Set(0, 0, ax); rv.Set(1, 0, ay); rv.Set(2, 0, az);
        using var r = new Mat();
        Cv2.Rodrigues(rv, r);
        var m = new double[16];
        for (var row = 0; row < 3; row++) for (var col = 0; col < 3; col++) m[row * 4 + col] = r.At<double>(row, col);
        m[3] = tx; m[7] = ty; m[11] = tz; m[15] = 1;
        return m;
    }

    private static double[] Inverse(double[] t)
    {
        var result = new double[16];
        result[15] = 1;
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) result[r * 4 + c] = t[c * 4 + r];
        for (var r = 0; r < 3; r++) result[r * 4 + 3] = -(result[r * 4] * t[3] + result[r * 4 + 1] * t[7] + result[r * 4 + 2] * t[11]);
        return result;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        result[15] = 1;
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++) result[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c];
            result[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] + a[r * 4 + 2] * b[11] + a[r * 4 + 3];
        }
        return result;
    }
    [Fact]
    public async Task HandEye3DRejectionRespectsRemovalCap()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var samples = BuildConsistentSampleList(truth, 10);
        samples[4].TargetToCamera[3] += 15;
        var json = JsonSerializer.Serialize(new { samples });
        var capped = await Solve(json, new { method = "DANIILIDIS", crossCheckMethod = "NONE", outlierRejection = "iterative", maxRejections = 0 });
        Assert.Equal(10L, capped.Outputs["usedSampleCount"].Require<long>("usedSampleCount"));
        Assert.Equal("[]", capped.Outputs["rejectedSampleIndicesJson"].Require<string>("rejectedSampleIndicesJson"));
    }

    [Fact]
    public async Task HandEye3DUnitMismatchRaisesWarning()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var clean = await Solve(JsonSerializer.Serialize(new { samples = BuildConsistentSampleList(truth, 10) }), new { method = "DANIILIDIS", crossCheckMethod = "NONE" });
        Assert.Equal(0, Convert.ToInt32(clean.Summary["warningCount"]));
        var samples = BuildConsistentSampleList(truth, 10);
        foreach (var sample in samples)
        {
            sample.TargetToCamera[3] *= 2;
            sample.TargetToCamera[7] *= 2;
            sample.TargetToCamera[11] *= 2;
        }
        var mismatched = await Solve(JsonSerializer.Serialize(new { samples }), new { method = "DANIILIDIS", crossCheckMethod = "NONE" });
        Assert.Equal(1, Convert.ToInt32(mismatched.Summary["warningCount"]));
        Assert.IsType<string>(mismatched.Summary["unitWarning"]);
    }

}