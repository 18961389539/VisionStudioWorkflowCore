using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
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
    public static IServiceCollection AddVisionStudioHost(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

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
        services.AddSingleton<StorageMaintenanceCoordinator>();
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
        services.AddSingleton(_ => new HardwareProvenanceStore(Path.Combine(environment.ContentRootPath, "data", "provenance")));
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
        services.AddSingleton(_ => new DeviceProfileStore(Path.Combine(environment.ContentRootPath, "data", "devices")));

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
        services.AddSingleton(_ => new DeviceLeaseRegistry());

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
