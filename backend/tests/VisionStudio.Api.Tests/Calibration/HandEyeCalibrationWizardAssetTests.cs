using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class HandEyeCalibrationWizardAssetTests
{
    [Fact]
    public async Task HandEyeSessionsPersistSolveSettingsAndRoundtrip()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var wizard = CreateWizard(env, db);
        var samplesJson = BuildSamplesJson(out var truth);

        var draft = wizard.SaveHandEyeSession(new HandEyeWizardSaveRequest(null, "草稿", """{"samples":[{"robot":{"x":0,"y":0,"thetaDeg":0},"vision":{"x":1,"y":1,"thetaDeg":0}}]}"""));
        Assert.Single(wizard.ListHandEyeSessions());

        var saved = wizard.SaveHandEyeSession(new HandEyeWizardSaveRequest(draft.SessionId, "正式会话", samplesJson, AngularWeight: 60, VisionScale: 1, RobustLoss: "tukey"));
        Assert.Equal(draft.SessionId, saved.SessionId);
        var loaded = wizard.GetHandEyeSession(saved.SessionId);
        Assert.Equal(60, loaded.AngularWeight);
        Assert.Equal("tukey", loaded.RobustLoss);

        var solved = await wizard.SolveHandEyeAsync(new CalibrationHandEyeSolveRequest(samplesJson, AngularWeight: 60, RobustLoss: "tukey"), default);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(solved));
        Assert.True(document.RootElement.GetProperty("converged").GetBoolean());
        Assert.InRange(document.RootElement.GetProperty("translationRms").GetDouble(), 0, 0.01);
        Assert.Equal("tukey", document.RootElement.GetProperty("summary").GetProperty("robustLoss").GetString());

        Assert.True(wizard.DeleteHandEyeSession(saved.SessionId));
        Assert.Throws<KeyNotFoundException>(() => wizard.GetHandEyeSession(saved.SessionId));
        Assert.Empty(wizard.ListHandEyeSessions());
    }

    [Fact]
    public async Task HandEyeAssetVersionsPublishAndRequireModelMatch()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var assets = new HandEyeCalibrationAssetStore(db);
        var wizard = CreateWizard(env, db, assets);
        var samplesJson = BuildSamplesJson(out var truth);
        var session = wizard.SaveHandEyeSession(new HandEyeWizardSaveRequest(null, "资产会话", samplesJson));

        var descriptor = await wizard.CreateHandEyeAssetAsync(new CreateHandEyeAssetFromSessionRequest(session.SessionId, "he-cell1", "Cell 1 Hand-Eye"), default);
        Assert.Equal(1, descriptor.LatestVersion);
        var version2 = await wizard.AddHandEyeVersionAsync("he-cell1", new SaveHandEyeVersionFromSessionRequest(session.SessionId, "re-teach"), default);
        Assert.Equal(2, version2.Version);
        await assets.PublishAsync("he-cell1", 2, "Publish", default);
        var published = await assets.GetPublishedAsync("he-cell1", default);
        Assert.Equal(2, published.Version);
        Assert.Equal(truth.X, published.Model.X, 2);

        var solved = await wizard.SolveHandEyeAsync(new CalibrationHandEyeSolveRequest(samplesJson), default);
        using var solvedDocument = JsonDocument.Parse(JsonSerializer.Serialize(solved));
        var calibrationJson = solvedDocument.RootElement.GetProperty("calibrationJson").GetString()!;
        await assets.RequireModelMatchAsync("he-cell1", 2, calibrationJson, default);

        var tampered = JsonSerializer.Serialize(published.Model with { X = published.Model.X + 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.RequireModelMatchAsync("he-cell1", 2, tampered, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.RequireModelMatchAsync("he-cell1", 99, calibrationJson, default));
    }

    [Fact]
    public async Task HandEyePinValidationReproducesAssetAndRejectsDrift()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var assets = new HandEyeCalibrationAssetStore(db);
        var wizard = CreateWizard(env, db, assets);
        var samplesJson = BuildSamplesJson(out _);
        var session = wizard.SaveHandEyeSession(new HandEyeWizardSaveRequest(null, "锁定会话", samplesJson));
        await wizard.CreateHandEyeAssetAsync(new CreateHandEyeAssetFromSessionRequest(session.SessionId, "he-pin", "Pinned"), default);

        var solver = new PlanarHandEyeCalibrationNode();
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("calibration.handEye2D"), solver, "builtin");
        var dependencies = CreateDependencies(env, db, registry, assets, solver);

        var pinned = HandEyeWorkflow(samplesJson, assetId: "he-pin", assetVersion: 1);
        await dependencies.ValidateCalibrationBindingsAsync(pinned, default);
        var manifest = await dependencies.CaptureAsync(pinned, default);
        Assert.Contains(manifest.Calibrations, x => x.AssetId == "hand-eye-calibrations:he-pin" && x.Version == 1);

        var tamperedSamples = samplesJson.Replace("\"x\":0,\"y\":0", "\"x\":1,\"y\":0");
        Assert.NotEqual(samplesJson, tamperedSamples);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(HandEyeWorkflow(tamperedSamples, assetId: "he-pin", assetVersion: 1), default));

        var connected = new WorkflowDefinition("he-connected", "Connected", pinned.Nodes,
            [new EdgeDefinition("e1", "upstream", "samplesJson", "handeye", "samplesJson")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(connected, default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.ValidateCalibrationBindingsAsync(HandEyeWorkflow(samplesJson, assetId: "he-pin", assetVersion: 0), default));
    }

    private static CalibrationWizardService CreateWizard(TempWebHostEnvironment env, SqliteMetadataDatabase db, HandEyeCalibrationAssetStore? handEyeAssets = null)
        => new(new CalibrationDetectBoardNode(), new CameraIntrinsicCalibrationNode(), new PlanarHandEyeCalibrationNode(),
            new HandEye3DCalibrationNode(), new DualCameraExtrinsicsNode(), new CameraManager(),
            new CameraIntrinsicAssetStore(db), env, handEyeAssets);

    private static RuntimeDependencyManifestService CreateDependencies(TempWebHostEnvironment env, SqliteMetadataDatabase db, VisionNodeRegistry registry, HandEyeCalibrationAssetStore assets, PlanarHandEyeCalibrationNode solver)
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
            handEyeAssets: assets, handEyeSolver: solver);
    }

    private static WorkflowDefinition HandEyeWorkflow(string samplesJson, string assetId, int assetVersion)
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["samplesJson"] = JsonSerializer.SerializeToElement(samplesJson),
            ["mode"] = JsonSerializer.SerializeToElement("eyeInHand"),
            ["handEyeAssetId"] = JsonSerializer.SerializeToElement(assetId)
        };
        if (assetVersion > 0) parameters["handEyeAssetVersion"] = JsonSerializer.SerializeToElement(assetVersion);
        return new WorkflowDefinition("he-pin", "Hand-Eye Pin",
            [new NodeDefinition("handeye", "calibration.handEye2D", "Hand-Eye", null, parameters)], []);
    }

    private static string BuildSamplesJson(out (double X, double Y, double Theta) truth)
    {
        var handEye = (X: 18.0, Y: -7.0, Theta: 0.31);
        truth = handEye;
        var target = (X: 210.0, Y: 45.0, Theta: -0.2);
        var robots = new[] { (X: 0.0, Y: 0.0, Theta: -0.7), (X: 70.0, Y: 20.0, Theta: -0.25), (X: 120.0, Y: -40.0, Theta: 0.2), (X: -30.0, Y: 90.0, Theta: 0.65), (X: 55.0, Y: 105.0, Theta: 1.05), (X: 160.0, Y: 35.0, Theta: 1.35) };
        var samples = robots.Select(r =>
        {
            var vision = Compose(Inverse(handEye), Compose(Inverse(r), target));
            return new { robot = new { x = r.X, y = r.Y, thetaDeg = r.Theta * 180 / Math.PI }, vision = new { x = vision.X, y = vision.Y, thetaDeg = vision.Theta * 180 / Math.PI } };
        }).ToArray();
        return JsonSerializer.Serialize(new { samples });
    }

    private static (double X, double Y, double Theta) Compose((double X, double Y, double Theta) a, (double X, double Y, double Theta) b)
    {
        var c = Math.Cos(a.Theta); var s = Math.Sin(a.Theta);
        return (a.X + c * b.X - s * b.Y, a.Y + s * b.X + c * b.Y, a.Theta + b.Theta);
    }

    private static (double X, double Y, double Theta) Inverse((double X, double Y, double Theta) p)
        => (-Math.Cos(p.Theta) * p.X - Math.Sin(p.Theta) * p.Y, Math.Sin(p.Theta) * p.X - Math.Cos(p.Theta) * p.Y, -p.Theta);
}