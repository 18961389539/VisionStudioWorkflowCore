using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Provenance;
using VisionStudio.Api.Hosting;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class HardwareProvenanceTests
{
    [Fact]
    public async Task VirtualCamera_ProvidesAdapterHardwareProvenance()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        var service = CreateService(env, cameras, new DeviceManager(), new RobotManager());

        var snapshot = await service.CaptureAsync("camera", "virtual-1");

        Assert.StartsWith("adapter:", snapshot.Provider);
        Assert.Equal("Complete", snapshot.Completeness);
        Assert.Equal("virtual-1", snapshot.Data.SerialNumber);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Fingerprint));
    }

    [Fact]
    public async Task PlantDeclaration_MergesExternalLensAndFirmwareAndChangesFingerprint()
    {
        using var env = new TempWebHostEnvironment();
        var devices = new DeviceManager();
        devices.Register(new VirtualModbusPlcDriver());
        var service = CreateService(env, new CameraManager(), devices, new RobotManager());
        var first = await service.CaptureAsync("device", "virtual-modbus-1");

        var second = await service.UpsertAsync("device", "virtual-modbus-1", new HardwareProvenanceDeclaration(
            FirmwareVersion: "PLC-FW-1.2.3",
            Attributes: new Dictionary<string, string> { ["fixtureRevision"] = "FIX-A", ["lightProgram"] = "LIGHT-07" }));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.True(second.HasManualDeclaration);
        Assert.Equal("PLC-FW-1.2.3", second.Data.FirmwareVersion);
        Assert.Equal("FIX-A", second.Data.Attributes!["fixtureRevision"]);
    }

    [Fact]
    public async Task RuntimeManifest_DetectsHardwareDeclarationDrift()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        var devices = new DeviceManager();
        var robots = new RobotManager();
        var provenance = CreateService(env, cameras, devices, robots);
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new AcquireImageNode(cameras), "builtin");
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var db = new SqliteMetadataDatabase(env);
        var dependencies = new RuntimeDependencyManifestService(
            registry, plugins, cameras, devices,
            new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices")), robots,
            new CalibrationAssetStore(db, new CalibrationWorkspaceService()), provenance);
        var workflow = new WorkflowDefinition("hardware", "Hardware", [new NodeDefinition("acquire", "image.acquire", "Acquire", null, null)], []);
        var published = await dependencies.CaptureAsync(workflow, default);

        await provenance.UpsertAsync("camera", "virtual-1", new HardwareProvenanceDeclaration(
            SerialNumber: "replacement-camera-42", FirmwareVersion: "FW-NEW"));
        var validation = await dependencies.ValidateAsync(published, workflow, default);

        Assert.False(validation.Compatible);
        Assert.Contains(validation.Drifts, x => x.Kind == "CameraHardware" && x.Id == "virtual-1");
    }

    [Fact]
    public async Task LegacyManifestSchema_IsRejectedUntilRepublishedWithHardwareIdentity()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        var devices = new DeviceManager();
        var robots = new RobotManager();
        var provenance = CreateService(env, cameras, devices, robots);
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new AcquireImageNode(cameras), "builtin");
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var db = new SqliteMetadataDatabase(env);
        var dependencies = new RuntimeDependencyManifestService(
            registry, plugins, cameras, devices,
            new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices")), robots,
            new CalibrationAssetStore(db, new CalibrationWorkspaceService()), provenance);
        var workflow = new WorkflowDefinition("legacy-hardware", "Legacy", [new NodeDefinition("acquire", "image.acquire", "Acquire", null, null)], []);
        var current = await dependencies.CaptureAsync(workflow, default);
        var legacy = current with
        {
            SchemaVersion = 1,
            Cameras = current.Cameras.Select(x => x with { Hardware = null }).ToArray(),
            ManifestHash = "legacy-manifest"
        };

        var validation = await dependencies.ValidateAsync(legacy, workflow, default);

        Assert.False(validation.Compatible);
        Assert.Contains(validation.Drifts, x => x.Kind == "ManifestSchema");
        Assert.Contains(validation.Drifts, x => x.Kind == "CameraHardware");
    }



    [Fact]
    public async Task RequiredLiveProbe_Unavailable_BlocksProductionCapture()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        using var probes = new VendorProvenanceProbeRegistry();
        probes.Register(new UnavailableVendorProvenanceProbe("camera", "virtual-1", "basler-pylon", true, "SDK unavailable"));
        var service = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")),
            cameras, new DeviceManager(), new RobotManager(), probes);

        var uiSnapshot = await service.CaptureAsync("camera", "virtual-1");
        Assert.True(uiSnapshot.LiveProbeConfigured);
        Assert.False(uiSnapshot.LiveProbeSucceeded);
        Assert.Equal("basler-pylon", uiSnapshot.LiveProbeProvider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureForProductionAsync("camera", "virtual-1"));
    }

    [Fact]
    public async Task OptionalLiveProbe_Unavailable_AllowsProductionCaptureWithDescriptorFallback()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        using var probes = new VendorProvenanceProbeRegistry();
        probes.Register(new UnavailableVendorProvenanceProbe("camera", "virtual-1", "hikrobot-mvs", false, "SDK unavailable"));
        var service = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")),
            cameras, new DeviceManager(), new RobotManager(), probes);

        var snapshot = await service.CaptureForProductionAsync("camera", "virtual-1");

        Assert.True(snapshot.LiveProbeConfigured);
        Assert.False(snapshot.LiveProbeRequired);
        Assert.Equal("virtual-1", snapshot.Data.SerialNumber);
    }

    [Fact]
    public async Task LiveProbeData_IsMergedIntoAutomaticChannel_AndCannotBeMaskedByDescriptor()
    {
        using var env = new TempWebHostEnvironment();
        var cameras = new CameraManager();
        cameras.Register(new VirtualCameraDevice());
        using var probes = new VendorProvenanceProbeRegistry();
        probes.Register(new FixedProbe("camera", "virtual-1", new VisionStudio.Engine.Provenance.HardwareProvenanceData(
            Manufacturer: "Basler", Model: "a2A2448-23gmBAS", SerialNumber: "40123456", FirmwareVersion: "3.2.1")));
        var service = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")),
            cameras, new DeviceManager(), new RobotManager(), probes);

        var snapshot = await service.CaptureAsync("camera", "virtual-1");

        Assert.True(snapshot.LiveProbeSucceeded);
        Assert.Contains("live:basler-pylon", snapshot.Provider);
        Assert.Equal("40123456", snapshot.Data.SerialNumber);
        Assert.Equal("3.2.1", snapshot.Data.FirmwareVersion);
        Assert.Equal("Basler", snapshot.Data.Manufacturer);
    }

    [Fact]
    public async Task RequiredVendorProbe_WithUnknownAsset_FailsBootstrap()
    {
        using var probes = new VendorProvenanceProbeRegistry();
        var options = Microsoft.Extensions.Options.Options.Create(new VendorProvenanceOptions
        {
            Basler = [new BaslerPylonProbeOptions { AssetId = "missing-camera", RequiredForProduction = true }]
        });
        var service = new VendorProvenanceBootstrapHostedService(
            probes, new CameraManager(), new DeviceManager(), new RobotManager(), options,
            NullLogger<VendorProvenanceBootstrapHostedService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));
    }

    [Fact]
    public async Task AbbRwsProbe_ReadsControllerRobotWareAndHashesRapidProgram()
    {
        var handler = new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = path switch
            {
                "/ctrl/identity" => Xml("<li class='ctrl-identity-info'><span class='ctrl-name'>IRC5-A</span><span class='ctrl-id'>CSE-001</span><span class='ctrl-type'>Real Controller</span><span class='ctrl-mac'>00:11:22:33:44:55</span></li>"),
                "/rw/system" => Xml("<li class='sys-system-li'><span class='rwversion'>6.15.06</span><span class='rwversionname'>RobotWare 6.15.06</span><span class='name'>VisionCell</span><span class='sysid'>{SYS-1}</span></li>"),
                "/rw/rapid/tasks/T_ROB1/program" => Xml("<li class='rap-program'><span class='name'>PickProgram</span><span class='entrypoint'>main</span></li>"),
                "/rw/rapid/modules?task=T_ROB1" => Xml("<li class='rap-module-info-li'><span class='name'>MainModule</span><span class='type'>ProgMod</span></li>"),
                "/rw/rapid/modules/MainModule?resource=module-extension&task=T_ROB1" => Xml("<li class='rap-module-extension'><span class='num-of-lines'>3</span><span class='max-num-of-col'>80</span><span class='count'>7</span></li>"),
                "/rw/rapid/modules/MainModule?task=T_ROB1&startrow=1&startcol=1&endrow=3&endcol=-1" => Xml("<li class='rap-mod-text'><span class='text'>MODULE MainModule\nPROC main()\nENDPROC\nENDMODULE</span></li>"),
                _ => throw new InvalidOperationException("Unexpected RWS request: " + path)
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://abb/") };
        var options = new AbbRwsProbeOptions
        {
            AssetId = "abb-1", BaseUrl = "http://abb/", Task = "T_ROB1", HashRapidProgram = true, RequiredForProduction = true
        };
        using var probe = new AbbRwsProvenanceProbe(options, 30, client);

        var result = await probe.CaptureAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("ABB", result.Data.Manufacturer);
        Assert.Equal("CSE-001", result.Data.SerialNumber);
        Assert.Equal("6.15.06", result.Data.ControllerVersion);
        Assert.Equal("PickProgram", result.Data.ProgramName);
        Assert.Equal("b3b25f80a52ccf2425e8208d42d43b6284581c68141d64f59cccc9eecd318e05", result.Data.ProgramHash);
        Assert.Equal("VisionCell", result.Data.Attributes!["systemName"]);
    }

    private static string Xml(string body) => $"<html xmlns='http://www.w3.org/1999/xhtml'><body><div class='state'><ul>{body}</ul></div></body></html>";

    private sealed class FixedProbe(string kind, string assetId, VisionStudio.Engine.Provenance.HardwareProvenanceData data) : IVendorHardwareProvenanceProbe
    {
        public string Kind => kind;
        public string AssetId => assetId;
        public string Provider => "basler-pylon";
        public bool RequiredForProduction => true;
        public ValueTask<VendorProvenanceProbeResult> CaptureAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new VendorProvenanceProbeResult(Provider, data, true, true));
        public void Dispose() { }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static HardwareProvenanceService CreateService(TempWebHostEnvironment env, CameraManager cameras, DeviceManager devices, RobotManager robots)
        => new(new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")), cameras, devices, robots, new VendorProvenanceProbeRegistry());
}
