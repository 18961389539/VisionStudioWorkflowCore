using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class HandEye3DCalibrationWizardAssetTests
{
    [Fact]
    public async Task HandEye3DSessionsPersistSolveSettingsAndRoundtrip()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var wizard = CreateWizard(env, db);
        var samplesJson = BuildSamplesJson();

        var draft = wizard.SaveHandEye3DSession(new HandEye3DWizardSaveRequest(null, "草稿", TinySamplesJson()));
        Assert.Single(wizard.ListHandEye3DSessions());

        var saved = wizard.SaveHandEye3DSession(new HandEye3DWizardSaveRequest(draft.SessionId, "正式会话", samplesJson, Method: "TSAI", OutlierRejection: "iterative", CrossCheckMethod: "PARK"));
        Assert.Equal(draft.SessionId, saved.SessionId);
        var loaded = wizard.GetHandEye3DSession(saved.SessionId);
        Assert.Equal("TSAI", loaded.Method);
        Assert.Equal("iterative", loaded.OutlierRejection);
        Assert.Equal("PARK", loaded.CrossCheckMethod);

        var solved = await wizard.SolveHandEye3DAsync(new CalibrationHandEye3DSolveRequest(samplesJson, Method: "DANIILIDIS", CrossCheckMethod: "TSAI"), default);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(solved));
        Assert.InRange(document.RootElement.GetProperty("translationRms").GetDouble(), 0, 0.01);
        Assert.True(document.RootElement.GetProperty("usedSampleCount").GetInt64() >= 6);
        Assert.Equal("DANIILIDIS", document.RootElement.GetProperty("summary").GetProperty("method").GetString());

        Assert.True(wizard.DeleteHandEye3DSession(saved.SessionId));
        Assert.Throws<KeyNotFoundException>(() => wizard.GetHandEye3DSession(saved.SessionId));
        Assert.Empty(wizard.ListHandEye3DSessions());
    }

    [Fact]
    public async Task HandEye3DAssetVersionsPublishAndRequireModelMatch()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var assets = new HandEye3DCalibrationAssetStore(db);
        var wizard = CreateWizard(env, db, assets);
        var samplesJson = BuildSamplesJson();
        var session = wizard.SaveHandEye3DSession(new HandEye3DWizardSaveRequest(null, "资产会话", samplesJson));

        var descriptor = await wizard.CreateHandEye3DAssetAsync(new CreateHandEye3DAssetFromSessionRequest(session.SessionId, "he3d-cell1", "Cell 1 3D Hand-Eye"), default);
        Assert.Equal(1, descriptor.LatestVersion);
        var version2 = await wizard.AddHandEye3DVersionAsync("he3d-cell1", new SaveHandEye3DVersionFromSessionRequest(session.SessionId, "re-teach"), default);
        Assert.Equal(2, version2.Version);
        await assets.PublishAsync("he3d-cell1", 2, "Publish", default);
        var published = await assets.GetPublishedAsync("he3d-cell1", default);
        Assert.Equal(2, published.Version);
        Assert.Equal(16, published.Model.Matrix.Length);

        var solved = await wizard.SolveHandEye3DAsync(new CalibrationHandEye3DSolveRequest(samplesJson), default);
        using var solvedDocument = JsonDocument.Parse(JsonSerializer.Serialize(solved));
        var calibrationJson = solvedDocument.RootElement.GetProperty("calibrationJson").GetString()!;
        await assets.RequireModelMatchAsync("he3d-cell1", 2, calibrationJson, default);

        var tampered = JsonSerializer.Serialize(published.Model with { Matrix = Tamper(published.Model.Matrix) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.RequireModelMatchAsync("he3d-cell1", 2, tampered, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.RequireModelMatchAsync("he3d-cell1", 99, calibrationJson, default));
    }

    [Fact]
    public async Task HandEye3DPinValidationReproducesAssetAndRejectsDrift()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var assets = new HandEye3DCalibrationAssetStore(db);
        var wizard = CreateWizard(env, db, assets);
        var samplesJson = BuildSamplesJson();
        var session = wizard.SaveHandEye3DSession(new HandEye3DWizardSaveRequest(null, "锁定会话", samplesJson));
        await wizard.CreateHandEye3DAssetAsync(new CreateHandEye3DAssetFromSessionRequest(session.SessionId, "he3d-pin", "Pinned"), default);

        var solver = new HandEye3DCalibrationNode();
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("calibration.handEye3D"), solver, "builtin");
        var dependencies = CreateDependencies(env, db, registry, assets, solver);

        var pinned = HandEye3DWorkflow(samplesJson, assetId: "he3d-pin", assetVersion: 1);
        await dependencies.ValidateCalibrationBindingsAsync(pinned, default);
        var manifest = await dependencies.CaptureAsync(pinned, default);
        Assert.Contains(manifest.Calibrations, x => x.AssetId == "hand-eye3d-calibrations:he3d-pin" && x.Version == 1);

        var tamperedNode = System.Text.Json.Nodes.JsonNode.Parse(samplesJson)!;
        var robotElement = tamperedNode["samples"]![0]!["robotToBase"]![3]!;
        tamperedNode["samples"]![0]!["robotToBase"]![3] = robotElement.GetValue<double>() + 1;
        var tamperedSamples = tamperedNode.ToJsonString();
        Assert.NotEqual(samplesJson, tamperedSamples);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(HandEye3DWorkflow(tamperedSamples, assetId: "he3d-pin", assetVersion: 1), default));

        var connected = new WorkflowDefinition("he3d-connected", "Connected", pinned.Nodes,
            [new EdgeDefinition("e1", "upstream", "samplesJson", "handeye3d", "samplesJson")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(connected, default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(HandEye3DWorkflow(samplesJson, assetId: "he3d-pin", assetVersion: 0), default));
    }

    private static double[] Tamper(double[] matrix)
    {
        var copy = (double[])matrix.Clone();
        copy[3] += 1;
        return copy;
    }

    private static CalibrationWizardService CreateWizard(TempWebHostEnvironment env, SqliteMetadataDatabase db, HandEye3DCalibrationAssetStore? handEye3DAssets = null)
        => new(new CalibrationDetectBoardNode(), new CameraIntrinsicCalibrationNode(), new PlanarHandEyeCalibrationNode(),
            new HandEye3DCalibrationNode(), new DualCameraExtrinsicsNode(), new CameraManager(),
            new CameraIntrinsicAssetStore(db), env, handEye3DAssets: handEye3DAssets);

    private static RuntimeDependencyManifestService CreateDependencies(TempWebHostEnvironment env, SqliteMetadataDatabase db, VisionNodeRegistry registry, HandEye3DCalibrationAssetStore assets, HandEye3DCalibrationNode solver)
    {
        var cameras = new CameraManager();
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var devices = new DeviceManager();
        var profiles = new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices"));
        var robots = new RobotManager();
        var calibrations = new CalibrationAssetStore(db, new CalibrationWorkspaceService());
        var hardware = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")), cameras, devices, robots, new VendorProvenanceProbeRegistry());
        return new RuntimeDependencyManifestService(registry, plugins, cameras, devices, profiles, robots, calibrations, hardware,
            handEye3DAssets: assets, handEye3DSolver: solver);
    }

    private static WorkflowDefinition HandEye3DWorkflow(string samplesJson, string assetId, int assetVersion)
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["samplesJson"] = JsonSerializer.SerializeToElement(samplesJson),
            ["mode"] = JsonSerializer.SerializeToElement("eyeInHand"),
            ["handEye3DAssetId"] = JsonSerializer.SerializeToElement(assetId)
        };
        if (assetVersion > 0) parameters["handEye3DAssetVersion"] = JsonSerializer.SerializeToElement(assetVersion);
        return new WorkflowDefinition("he3d-pin", "3D Hand-Eye Pin",
            [new NodeDefinition("handeye3d", "calibration.handEye3D", "Hand-Eye 3D", null, parameters)], []);
    }

    private static string TinySamplesJson()
        => JsonSerializer.Serialize(new { samples = new[] { new { robotToBase = Rigid(0, 0, 0, 0, 0, 0), targetToCamera = Rigid(0, 0, 0, 1, 1, 1) } } });

    private static string BuildSamplesJson()
    {
        var truth = Rigid(0.2, -0.15, 0.1, 18, -7, 12);
        var target = Rigid(0.3, 0.1, -0.2, 210, 45, -30);
        var samples = new List<object>();
        for (var i = 0; i < 10; i++)
        {
            var robot = Rigid(0.12 * i, 0.08 * (i % 3) - 0.1, 0.05 * i + 0.03 * (i % 2), 12 * i, 6 * (i % 4), 3 * i);
            var vision = Multiply(Multiply(Inverse(truth), Inverse(robot)), target);
            samples.Add(new { robotToBase = robot, targetToCamera = vision });
        }
        return JsonSerializer.Serialize(new { samples });
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
}