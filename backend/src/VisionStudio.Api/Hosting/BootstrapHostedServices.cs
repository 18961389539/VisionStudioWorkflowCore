using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Api.Media;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Algorithms;

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
        var algorithms = VisionAlgorithmRuntime.Initialize();
        logger.LogInformation("Vision algorithms: {Backend} {Version}; inference: {InferenceBackend} {InferenceVersion}", algorithms.TraditionalBackend, algorithms.NativeVersion, algorithms.InferenceBackend, algorithms.InferenceVersion);
        await RegisterCamerasAsync(cancellationToken);
        await RegisterRobotsAsync(cancellationToken);
        await RegisterDevicesAsync(cancellationToken);
        RegisterBuiltInNodes();
        LoadPlugins();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        VisionAlgorithmRuntime.ClearModelCache();
        return Task.CompletedTask;
    }

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

        if (!RobotSimulatorPolicy.ShouldStart(configuration, environment, out var reason))
        {
            // 生产环境里配了 Enabled=true 不能静默忽略：现场必须能从日志看到"我配的东西没生效"。
            if (reason.Length > 0) logger.LogWarning("Built-in TCP robot simulator will not start: {Reason}", reason);
            return;
        }

        var simulator = services.GetRequiredService<TcpRobotSimulatorServer>();
        await simulator.StartAsync(cancellationToken);
        robots.Register(new TcpJsonRobotAdapter("tcp-sim-1", "TCP ABB Simulator", "127.0.0.1", simulator.Port, simulated: true));
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

        // R3-04（2026-10-13 R3 审查）：损坏/不可读的配置文件不再静默跳过——逐条记录文件名
        // （即设备 ID 提示）与解析原因，原文件保留供修复；健康接口把该状态表达为配置完整性降级。
        foreach (var problem in profiles.Problems)
        {
            logger.LogError(
                "Device profile '{File}' could not be loaded ({Error}); the device was NOT registered and the file was preserved for repair.",
                problem.FileName, problem.Error);
        }
    }

    /// <summary>
    /// Registers every built-in executor discovered in the engine assembly and refuses to start when the
    /// catalog and the executors disagree. The previous hand-written list could drift silently: a node
    /// missing here only failed when a workflow first used it, not at startup.
    /// </summary>
    private void RegisterBuiltInNodes()
    {
        var registry = services.GetRequiredService<VisionNodeRegistry>();
        var executors = services.GetServices<IVisionNodeExecutor>().ToArray();
        var drift = BuiltInNodeExecutors.FindDrift(executors, BuiltInNodeCatalog.Items);
        if (drift.Count > 0)
        {
            throw new InvalidOperationException(
                "Built-in node catalog and executor registrations are out of sync:"
                + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", drift));
        }

        // 已合并节点的别名执行器（Nodes/MergedNodeAliases.cs）没有目录条目，不进注册表：
        // 历史文档在编译入口已被 NodeTypeMigration 改写为规范类型，别名只服务直接构造执行器的调用方。
        var aliasTypes = NodeTypeMigration.ReplacementMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var executor in executors)
        {
            if (aliasTypes.Contains(executor.Type)) continue;
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "builtin");
        }

        logger.LogInformation("Registered {Count} built-in node executors ({AliasCount} merged-node aliases skipped).",
            executors.Length - aliasTypes.Count, aliasTypes.Count);
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

/// <summary>
/// 内置 TCP 机器人模拟器的启用策略（2026-11-12）。
///
/// 故障形态：<c>appsettings.json</c> 默认 <c>RobotTcpSimulator:Enabled=true</c>，而 Production 覆盖文件
/// 里没有这一节，于是现场部署会在 40501 端口开一个 TCP 机器人服务，并注册 <c>tcp-sim-1</c> 适配器。
/// 工艺员在机器人面板里选中它、下发目标、看到 InPosition——产线却没有任何动作，排查方向被彻底带偏。
///
/// 因此：生产一律不启动模拟器，配置里写了也跳过并留下告警（fail-closed，但不把"配置写错"升级成
/// "整线停摆"）。现场演示虚拟机器人请使用 Development 环境。
/// </summary>
internal static class RobotSimulatorPolicy
{
    internal static bool ShouldStart(IConfiguration configuration, IWebHostEnvironment environment)
        => ShouldStart(configuration, environment, out _);

    internal static bool ShouldStart(IConfiguration configuration, IWebHostEnvironment environment, out string reason)
    {
        var configured = configuration.GetValue("RobotTcpSimulator:Enabled", true);
        if (!configured)
        {
            reason = string.Empty; // 显式关闭：无需告警。
            return false;
        }

        if (environment.IsProduction())
        {
            reason = "RobotTcpSimulator:Enabled=true is ignored in Production; virtual robots must not be selectable in a field deployment.";
            return false;
        }

        if (environment.IsEnvironment("Testing"))
        {
            reason = "disabled in the Testing environment to keep tests hermetic.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
