using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Api.Media;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Hosting;

public sealed class StorageMigrationHostedService(
    LegacyStorageMigrationService migration,
    IWebHostEnvironment environment,
    ILogger<StorageMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing")) return;
        logger.LogInformation("Running legacy metadata migration before runtime bootstrap.");
        await migration.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class RuntimeBootstrapHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<RuntimeBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RegisterCamerasAsync(cancellationToken);
        await RegisterRobotsAsync(cancellationToken);
        await RegisterDevicesAsync(cancellationToken);
        RegisterBuiltInNodes();
        LoadPlugins();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterCamerasAsync(CancellationToken cancellationToken)
    {
        var cameras = services.GetRequiredService<CameraManager>();
        cameras.Register(new VirtualCameraDevice());

        var bundledFrames = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../../../samples/camera_frames"));
        if (Directory.Exists(bundledFrames))
        {
            var media = services.GetRequiredService<MediaLibraryService>();
            media.SeedCollectionFromDirectory("bundled-demo", bundledFrames);
            var source = media.ResolveSource("media://bundled-demo");
            cameras.Register(new FileCameraDevice("file-demo", "Bundled File Camera", source.Source, source.PhysicalPath));
        }

        var configured = configuration.GetSection("CameraAdapters").Get<CameraAdapterOptions>() ?? new CameraAdapterOptions();
        var providers = services.GetServices<ICameraAdapterProvider>().ToDictionary(x => x.Driver, StringComparer.OrdinalIgnoreCase);
        foreach (var item in configured.Cameras.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(item.Driver) || string.IsNullOrWhiteSpace(item.Id))
                throw new InvalidOperationException("Configured vendor camera requires Driver and Id.");
            if (!providers.TryGetValue(item.Driver, out var provider))
                throw new InvalidOperationException($"Configured camera '{item.Id}' references unknown adapter driver '{item.Driver}'.");
            if (!provider.IsSdkAvailable)
                throw new InvalidOperationException($"Configured camera '{item.Id}' requires unavailable adapter '{provider.Driver}': {provider.SdkError}");
            var device = provider.Create(new CameraAdapterRegistration(
                item.Id, item.Name, item.SerialNumber, item.UserDefinedName, item.DeviceKey, item.Settings));
            cameras.Register(device, Math.Clamp(item.RingCapacity, 2, 64));
            if (!string.IsNullOrWhiteSpace(item.FeatureProfileId))
            {
                var profiles = services.GetRequiredService<CameraFeatureProfileStore>();
                var saved = await profiles.GetAsync(item.FeatureProfileId, cancellationToken);
                if (!string.Equals(saved.Driver, provider.Driver, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Configured camera '{item.Id}' uses driver '{provider.Driver}' but feature profile '{saved.Id}' targets '{saved.Driver}'.");
                await cameras.OpenAsync(item.Id, cancellationToken);
                try { await cameras.ApplyCommissioningProfileAsync(item.Id, saved.Profile, cancellationToken); }
                finally { await cameras.CloseAsync(item.Id, cancellationToken); }
                logger.LogInformation("Applied camera feature profile {ProfileId} ({ProfileHash}) to {CameraId}", saved.Id, saved.ProfileHash, item.Id);
            }
            logger.LogInformation("Registered configured vendor camera {CameraId} using {Driver}", item.Id, provider.Driver);
        }
    }

    private async Task RegisterRobotsAsync(CancellationToken cancellationToken)
    {
        var robots = services.GetRequiredService<RobotManager>();
        robots.Register(new VirtualAbbRobotAdapter());
        robots.Register(new VirtualPlcRobotBridgeAdapter());

        var enabled = !environment.IsEnvironment("Testing") && configuration.GetValue("RobotTcpSimulator:Enabled", true);
        if (!enabled) return;

        var simulator = services.GetRequiredService<TcpRobotSimulatorServer>();
        await simulator.StartAsync(cancellationToken);
        robots.Register(new TcpJsonRobotAdapter("tcp-sim-1", "TCP ABB Simulator", "127.0.0.1", simulator.Port));
    }

    private async Task RegisterDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = services.GetRequiredService<DeviceManager>();
        var profiles = services.GetRequiredService<DeviceProfileStore>();
        devices.Register(new VirtualModbusPlcDriver());

        foreach (var profile in await profiles.ListAsync(cancellationToken))
        {
            try
            {
                var driver = DeviceDriverFactory.Create(profile);
                devices.Register(driver);
                if (profile.Settings is not null) devices.ApplySettings(profile.Id, profile.Settings);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to restore device profile {DeviceId}", profile.Id);
            }
        }
    }

    private void RegisterBuiltInNodes()
    {
        var registry = services.GetRequiredService<VisionNodeRegistry>();
        IVisionNodeExecutor[] builtIns =
        [
            services.GetRequiredService<AcquireImageNode>(),
            services.GetRequiredService<SynchronizedCaptureNode>(),
            services.GetRequiredService<FrameSetImageNode>(),
            services.GetRequiredService<SyntheticImageNode>(),
            services.GetRequiredService<ThresholdNode>(),
            services.GetRequiredService<LargestBlobNode>(),
            services.GetRequiredService<EdgeNode>(),
            services.GetRequiredService<LineNode>(),
            services.GetRequiredService<CircleNode>(),
            services.GetRequiredService<CaliperNode>(),
            services.GetRequiredService<RotatedRectangleNode>(),
            services.GetRequiredService<IntersectionNode>(),
            services.GetRequiredService<DistanceNode>(),
            services.GetRequiredService<AngleNode>(),
            services.GetRequiredService<PlanarCalibrationNode>(),
            services.GetRequiredService<TransformPointNode>(),
            services.GetRequiredService<TransformLineNode>(),
            services.GetRequiredService<TransformPoseNode>(),
            services.GetRequiredService<CoordinatePointNode>(),
            services.GetRequiredService<CoordinateDistanceNode>(),
            services.GetRequiredService<ConstantTransformNode>(),
            services.GetRequiredService<InverseTransformNode>(),
            services.GetRequiredService<ComposeTransformNode>(),
            services.GetRequiredService<FrameTreeNode>(),
            services.GetRequiredService<ResolveFrameTransformNode>(),
            services.GetRequiredService<CoordinatePoseNode>(),
            services.GetRequiredService<PoseToTransformNode>(),
            services.GetRequiredService<TransformCoordinatePointNode>(),
            services.GetRequiredService<TransformCoordinatePoseNode>(),
            services.GetRequiredService<RobotGuidance2DNode>(),
            services.GetRequiredService<RobotCurrentPoseNode>(),
            services.GetRequiredService<RobotExecuteTargetNode>(),
            services.GetRequiredService<J4TcpCompensationNode>(),
            services.GetRequiredService<DeviceReadTagNode>(),
            services.GetRequiredService<DeviceWriteTagNode>(),
            services.GetRequiredService<DeviceWaitTagNode>(),
            services.GetRequiredService<DeviceWriteVisionResultNode>(),
            services.GetRequiredService<IfConditionNode>(),
            services.GetRequiredService<ParallelMarkerNode>(),
            services.GetRequiredService<JoinNode>(),
            services.GetRequiredService<ResultDispositionNode>()
        ];

        foreach (var executor in builtIns)
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");
    }

    private void LoadPlugins()
    {
        var pluginRoot = Path.Combine(environment.ContentRootPath, "plugins");
        var packages = services.GetRequiredService<PluginPackageService>();
        packages.PrepareRuntimePluginRoot(pluginRoot);
        var plugins = services.GetRequiredService<PluginManager>();
        plugins.LoadAll(pluginRoot);
    }
}

public sealed class ProductionRuntimeHostedService(
    ProductionRuntimeService production,
    ILogger<ProductionRuntimeHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => production.AutoStartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await production.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Production runtime shutdown did not complete cleanly.");
        }
    }
}

public sealed class VendorProvenanceBootstrapHostedService(
    VendorProvenanceProbeRegistry registry,
    CameraManager cameras,
    DeviceManager devices,
    RobotManager robots,
    Microsoft.Extensions.Options.IOptions<VendorProvenanceOptions> configured,
    ILogger<VendorProvenanceBootstrapHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = configured.Value;
        foreach (var item in options.Basler.Where(x => x.Enabled))
            RegisterSafe("camera", item.AssetId, "basler-pylon", item.RequiredForProduction,
                () => new BaslerPylonProvenanceProbe(item, options.CacheSeconds));
        foreach (var item in options.Hikrobot.Where(x => x.Enabled))
            RegisterSafe("camera", item.AssetId, "hikrobot-mvs", item.RequiredForProduction,
                () => new HikrobotMvsProvenanceProbe(item, options.CacheSeconds));
        foreach (var item in options.AbbRws.Where(x => x.Enabled))
            RegisterSafe("robot", item.AssetId, "abb-rws", item.RequiredForProduction,
                () => new AbbRwsProvenanceProbe(item, options.CacheSeconds));
        logger.LogInformation("Vendor hardware provenance probes registered: {Count}", registry.List().Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RegisterSafe(string kind, string assetId, string provider, bool required, Func<IVendorHardwareProvenanceProbe> factory)
    {
        if (string.IsNullOrWhiteSpace(assetId) || !AssetExists(kind, assetId))
        {
            var reason = string.IsNullOrWhiteSpace(assetId)
                ? $"{provider} provenance probe has an empty AssetId."
                : $"{provider} provenance probe references unknown {kind} AssetId '{assetId}'. Runtime assets must be registered before vendor provenance bootstrap.";
            if (required) throw new InvalidOperationException("Required " + reason);
            logger.LogWarning("Skipping optional vendor provenance probe: {Reason}", reason);
            return;
        }
        try { registry.Register(factory()); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize {Provider} provenance probe for {Kind} {AssetId}; registering an unavailable probe.", provider, kind, assetId);
            registry.Register(new UnavailableVendorProvenanceProbe(kind, assetId, provider, required, ex.Message));
        }
    }

    private bool AssetExists(string kind, string assetId) => kind switch
    {
        "camera" => cameras.List().Any(x => string.Equals(x.Id, assetId, StringComparison.OrdinalIgnoreCase)),
        "device" => devices.List().Any(x => string.Equals(x.Id, assetId, StringComparison.OrdinalIgnoreCase)),
        "robot" => robots.List().Any(x => string.Equals(x.Id, assetId, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };
}
