using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Camera.Basler;
using VisionStudio.Camera.Hikrobot;
using VisionStudio.Api.Diagnostics;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine;
using VisionStudio.Api.Security;
using VisionStudio.Api.Media;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Hosting;

public static class VisionStudioServiceRegistration
{
    /// <summary>R08：loopback 判定（含 IPv6 字面量与 localhost 通配）。</summary>
    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("127.0.0.1", StringComparison.Ordinal) ||
           host.Equals("::1", StringComparison.Ordinal) ||
           host.Equals("[::1]", StringComparison.Ordinal) ||
           host.Equals("*.localhost", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddVisionStudioHost(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // 生产环境禁止免密码管理员自动登录：开发便利开关（appsettings.json 默认 true）不得
        // 随配置继承进入现场。Production（含未显式设置 ASPNETCORE_ENVIRONMENT 的默认情形）
        // 下 AutoLoginAdmin=true 直接拒绝启动；本地开发请使用 Development 环境（run-dev.ps1）。
        var securityOptions = configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
        if (environment.IsProduction() && securityOptions.Enabled && securityOptions.AutoLoginAdmin)
        {
            throw new InvalidOperationException(
                "Security:AutoLoginAdmin=true is not allowed in the Production environment. " +
                "Disable it in appsettings.Production.json, or run local development with ASPNETCORE_ENVIRONMENT=Development.");
        }

        // R08：网络交付边界。默认监听 loopback；把控制端口暴露到网络必须使用 HTTPS——
        // 明文 HTTP 会同时泄露账户口令与会话 cookie（Secure 无法在 HTTP 上生效）。
        // 隔离测试网络如需明文，可显式设置 Security:AllowInsecureRemoteTransport=true。
        if (environment.IsProduction() && securityOptions.Enabled && !securityOptions.AllowInsecureRemoteTransport)
        {
            var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? string.Empty;
            var insecure = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) &&
                            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                            !IsLoopbackHost(uri.Host))
                .ToArray();
            if (insecure.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to start: plaintext HTTP is bound to a non-loopback address ({string.Join(", ", insecure)}). " +
                    "Account passwords and session cookies would travel unprotected (a Secure cookie cannot be set over HTTP). " +
                    "Bind to loopback (http://127.0.0.1:5080) or terminate TLS in front of the host (HTTPS endpoint / trusted reverse proxy), " +
                    "or set Security:AllowInsecureRemoteTransport=true for an isolated test network.");
            }
        }

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        // 持久滚动文件日志（data/logs，按天、保留 14 天）：现场故障后可直接取日志导出，
        // 不依赖控制台留存。写入失败绝不影响主流程，但丢弃/失败计数经 /api/health 暴露（F10）。
        var fileLogger = new RollingFileLoggerProvider(
            Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "logs"));
        services.AddLogging(builder => builder.AddProvider(fileLogger));
        services.AddSingleton(fileLogger);

        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.Configure<SecurityOptions>(configuration.GetSection("Security"));
        services.AddSingleton<SecurityStore>();
        services.AddSingleton<AuditEventStore>();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = VisionStudioAuthenticationHandler.Scheme;
            options.DefaultChallengeScheme = VisionStudioAuthenticationHandler.Scheme;
            options.DefaultForbidScheme = VisionStudioAuthenticationHandler.Scheme;
        }).AddScheme<AuthenticationSchemeOptions, VisionStudioAuthenticationHandler>(VisionStudioAuthenticationHandler.Scheme, _ => { });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(SecurityPolicies.Operator, policy => policy.RequireRole(SecurityRoles.Operator, SecurityRoles.Engineer, SecurityRoles.Administrator))
            .AddPolicy(SecurityPolicies.Engineer, policy => policy.RequireRole(SecurityRoles.Engineer, SecurityRoles.Administrator))
            .AddPolicy(SecurityPolicies.Administrator, policy => policy.RequireRole(SecurityRoles.Administrator));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("media-import", _ => RateLimitPartition.GetConcurrencyLimiter(
                partitionKey: "global-media-import",
                factory: _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 1,
                    QueueLimit = 0
                }));
        });
        var mediaMaxBatchBytes = Math.Max(
            configuration.GetValue<long?>("MediaLibrary:MaxBatchBytes") ?? 512L * 1024 * 1024,
            configuration.GetValue<long?>("MediaLibrary:MaxImportBytes") ?? 100L * 1024 * 1024);
        services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = mediaMaxBatchBytes + 1024 * 1024);
        services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = mediaMaxBatchBytes + 1024 * 1024);
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddOpenApi();
        services.AddSignalR();
        services.Configure<TraceRetentionOptions>(configuration.GetSection("TraceRetention"));
        services.AddOptions<ProductionTraceOptions>().Bind(configuration.GetSection("ProductionTrace"))
            .Validate(o => o.QueueCapacity is >= 1 and <= 1024 && o.MaxRawArtifactBytesPerRun > 0, "Invalid production trace queue limits.")
            .ValidateOnStart();
        services.Configure<StorageMaintenanceOptions>(configuration.GetSection("Storage"));
        services.Configure<VendorProvenanceOptions>(configuration.GetSection("VendorProvenance"));
        services.Configure<CameraAdapterOptions>(configuration.GetSection("CameraAdapters"));
        services.Configure<PluginPackageOptions>(configuration.GetSection("PluginPackages"));

        services.AddSingleton<SqliteMetadataDatabase>();
        services.AddSingleton<LegacyStorageMigrationService>();
        // F03：失败锁持久化到数据根——重启后仍保留，直到受控恢复入口（或人工）完成核对后解除；
        // 纯内存的旧实现重启即丢锁，半恢复的主机会在多实例资产不一致的状态下放行写入。
        // Q08：同一目录下另存两个标记——restore-transaction.json（恢复中途被终止 → 启动即锁）
        // 与 restart-pending.json（恢复成功但未重启 → 业务 503 直到重启）。
        var maintenanceDir = Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), ".storage-maintenance");
        services.AddSingleton(sp => new StorageMaintenanceCoordinator(
            Path.Combine(maintenanceDir, "failure-lock.json"),
            sp.GetRequiredService<ILogger<StorageMaintenanceCoordinator>>(),
            Path.Combine(maintenanceDir, "restart-pending.json"),
            Path.Combine(maintenanceDir, "restore-transaction.json")));
        services.AddSingleton<StorageCapacityService>();

        services.AddSingleton<VisionNodeRegistry>();
        services.AddSingleton<CameraManager>();
        services.AddSingleton<CameraFeatureProfileStore>();
        var cameraAdapters = configuration.GetSection("CameraAdapters").Get<CameraAdapterOptions>() ?? new CameraAdapterOptions();
        services.AddSingleton(_ => new BaslerPylonCameraAdapterProvider(cameraAdapters.BaslerAssemblyPath));
        services.AddSingleton(_ => new HikrobotMvsCameraAdapterProvider(cameraAdapters.HikrobotAssemblyPath));
        services.AddSingleton<ICameraAdapterProvider>(sp => sp.GetRequiredService<BaslerPylonCameraAdapterProvider>());
        services.AddSingleton<ICameraAdapterProvider>(sp => sp.GetRequiredService<HikrobotMvsCameraAdapterProvider>());
        services.AddSingleton<ICameraActionCommandProvider>(sp => sp.GetRequiredService<BaslerPylonCameraAdapterProvider>());
        services.AddSingleton<ICameraActionCommandProvider>(sp => sp.GetRequiredService<HikrobotMvsCameraAdapterProvider>());
        services.AddSingleton<CameraSynchronizationGroupStore>();
        services.AddSingleton<CameraSynchronizationRunStore>();
        services.AddSingleton<CameraSynchronizationCommissioningStore>();
        services.AddSingleton<CameraSynchronizationService>();
        services.AddSingleton<CameraSynchronizationCommissioningService>();
        services.AddSingleton<GigENetworkDiagnosticsService>();
        services.AddSingleton<ISynchronizedFrameSetService>(sp => sp.GetRequiredService<CameraSynchronizationService>());
        services.AddSingleton<MediaLibraryService>();
        services.AddSingleton(_ => new HardwareProvenanceStore(Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "provenance")));
        services.AddSingleton<VendorProvenanceProbeRegistry>();
        services.AddSingleton<HardwareProvenanceService>();
        services.AddSingleton<RobotCommandTraceStore>();
        services.AddSingleton<IRobotCommandObserver>(sp => sp.GetRequiredService<RobotCommandTraceStore>());
        services.AddSingleton<RobotManager>();
        services.AddSingleton<DeviceManager>();

        services.AddSingleton<IAssetHealthProvider, CameraAssetHealthProvider>();
        services.AddSingleton<IAssetHealthProvider, DeviceAssetHealthProvider>();
        services.AddSingleton<IAssetHealthProvider, RobotAssetHealthProvider>();
        services.AddSingleton<DiagnosticsCenterService>();
        services.AddSingleton(_ => new DeviceProfileStore(Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "devices")));

        var tcpSimulatorPort = configuration.GetValue("RobotTcpSimulator:Port", 40501);
        services.AddSingleton(_ => new TcpRobotSimulatorServer(tcpSimulatorPort));

        AddBuiltInNodes(services);
        services.Configure<PluginWorkerOptions>(configuration.GetSection("PluginWorkers"));
        services.AddSingleton<PluginWorkerSupervisor>();
        services.AddSingleton<PluginBenchmarkStore>();
        services.AddSingleton<PluginPerformanceBenchmarkService>();
        services.AddSingleton<PluginManager>();
        services.AddSingleton<PluginPackageService>();

        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddSingleton(sp => new WorkflowPlanCache(
            sp.GetRequiredService<VisionWorkflowCompiler>(),
            sp.GetRequiredService<WorkflowCore.Interface.IDefinitionLoader>(),
            sp.GetRequiredService<WorkflowCore.Interface.IWorkflowRegistry>(),
            sp.GetRequiredService<VisionNodeRegistry>(),
            configuration.GetValue("WorkflowPlanCache:Capacity", 128)));
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        services.AddTransient<ModuleAwareVisionWorkflowRunner>();
        services.AddTransient<IVisionWorkflowRunner>(sp => sp.GetRequiredService<ModuleAwareVisionWorkflowRunner>());
        services.AddSingleton<WorkflowDebugSessionService>();
        services.AddSingleton<DebugSessionService>();
        // R02：租约接管必须由设备层活动状态判定（TTL 到期不是"设备可用"的证据）。
        services.AddSingleton<IDeviceActivityProbe, DeviceActivityProbe>();
        services.AddSingleton(sp => new DeviceLeaseRegistry(
            timeProvider: null,
            probe: sp.GetRequiredService<IDeviceActivityProbe>()));

        services.AddSingleton<RunStore>();
        services.AddSingleton<WorkflowStore>();
        services.AddSingleton<RecipeParameterStore>();
        services.AddSingleton<JobStore>(sp => new JobStore(
            sp.GetRequiredService<SqliteMetadataDatabase>(),
            sp.GetRequiredService<RecipeParameterStore>()));
        services.AddSingleton<ProductRecipeStore>();
        services.AddSingleton<WorkflowModuleStore>();
        services.AddSingleton<WorkflowModuleExpander>();
        services.AddSingleton<WorkflowModuleAuthoringService>();
        services.AddSingleton<RuntimeDependencyManifestService>();
        services.AddSingleton<TraceabilityStore>();
        services.AddSingleton<RunTraceRecorder>();
        services.AddSingleton<RunObservabilityService>();
        services.AddSingleton<TraceAnalysisService>();
        services.AddSingleton<InvestigationCaseService>();
        services.AddSingleton<OfflineReplayService>();
        services.AddSingleton<DatasetValidationStore>();
        services.AddSingleton<DatasetValidationService>();
        services.AddSingleton<ParameterTuningService>();
        services.AddSingleton<AlarmStore>();
        services.AddSingleton<ProductionRuntimeConfigStore>();
        services.AddSingleton<ProductionPtpDriftGuardService>();
        services.AddSingleton<ProductionSynchronizationHealthGuardService>();
        // F01/F02：设备动作安全状态的持久化（副作用故障标记 + 锁定清单跨重启保留）。
        services.AddSingleton<DeviceActionSafetyStore>();
        // R01：手动/临时运行的动作意图（独立文件）与统一动作授权闸门。
        services.AddSingleton<ManualActionIntentStore>();
        services.AddSingleton<DeviceActionAuthorizationService>();
        services.AddSingleton<ProductionRuntimeService>();
        services.AddSingleton<StorageBackupService>();
        services.AddSingleton<CalibrationWorkspaceService>();
        services.AddSingleton<CalibrationAssetStore>(sp => new CalibrationAssetStore(
            sp.GetRequiredService<SqliteMetadataDatabase>(),
            sp.GetRequiredService<CalibrationWorkspaceService>()));
        services.AddSingleton<ICalibrationPointProvider, VirtualCalibrationPointProvider>();

        services.AddCors(options =>
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:5173"];
            options.AddDefaultPolicy(policy => policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod());
        });

        // Start order matters: migrate persisted metadata first, then register runtime assets/plugins,
        // start Diagnostics observation, then allow Production AutoStart. Trace retention starts after the runtime is initialized.
        services.AddHostedService<StorageMigrationHostedService>();
        services.AddHostedService<RuntimeBootstrapHostedService>();
        services.AddHostedService<VendorProvenanceBootstrapHostedService>();
        services.AddHostedService<DiagnosticsCenterService>(sp => sp.GetRequiredService<DiagnosticsCenterService>());
        services.AddHostedService<ProductionRuntimeHostedService>();
        services.AddHostedService<TraceRetentionHostedService>();
        services.AddHostedService<StorageCapacityHostedService>();

        return services;
    }

    private static void AddBuiltInNodes(IServiceCollection services)
    {
        services.AddSingleton<AcquireImageNode>();
        services.AddSingleton<SynchronizedCaptureNode>();
        services.AddSingleton<FrameSetImageNode>();
        services.AddSingleton<SyntheticImageNode>();
        services.AddSingleton<ThresholdNode>();
        services.AddSingleton<LargestBlobNode>();
        services.AddSingleton<EdgeNode>();
        services.AddSingleton<LineNode>();
        services.AddSingleton<CircleNode>();
        services.AddSingleton<CaliperNode>();
        services.AddSingleton<RotatedRectangleNode>();
        services.AddSingleton<IntersectionNode>();
        services.AddSingleton<DistanceNode>();
        services.AddSingleton<AngleNode>();
        services.AddSingleton<PlanarCalibrationNode>();
        services.AddSingleton<TransformPointNode>();
        services.AddSingleton<TransformLineNode>();
        services.AddSingleton<TransformPoseNode>();
        services.AddSingleton<CoordinatePointNode>();
        services.AddSingleton<CoordinateDistanceNode>();
        services.AddSingleton<ConstantTransformNode>();
        services.AddSingleton<InverseTransformNode>();
        services.AddSingleton<ComposeTransformNode>();
        services.AddSingleton<FrameTreeNode>();
        services.AddSingleton<ResolveFrameTransformNode>();
        services.AddSingleton<CoordinatePoseNode>();
        services.AddSingleton<PoseToTransformNode>();
        services.AddSingleton<TransformCoordinatePointNode>();
        services.AddSingleton<TransformCoordinatePoseNode>();
        services.AddSingleton<RobotGuidance2DNode>();
        services.AddSingleton<RobotCurrentPoseNode>();
        services.AddSingleton<RobotExecuteTargetNode>();
        services.AddSingleton<J4TcpCompensationNode>();
        services.AddSingleton<DeviceReadTagNode>();
        services.AddSingleton<DeviceWriteTagNode>();
        services.AddSingleton<DeviceWaitTagNode>();
        services.AddSingleton<DeviceWriteVisionResultNode>();
        services.AddSingleton<IfConditionNode>();
        services.AddSingleton<ParallelMarkerNode>();
        services.AddSingleton<JoinNode>();
        services.AddSingleton<ResultDispositionNode>();
    }
}
