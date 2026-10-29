using VisionStudio.Abstractions;
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
    /// <summary>
    /// R3-02锛?026-10-13 R3 瀹℃煡锛夛細鐢熶骇鏄庢枃鐩戝惉杈圭晫鐨?*瀹屾暣鏉ユ簮鏋氫妇**銆?
    ///
    /// 实测监听优先级（本机实验，2026-10-13，.NET 10 Kestrel）：
    ///   `Kestrel:Endpoints:*:Url` 锛?`urls`锛?-urls / ASPNETCORE_URLS / 閰嶇疆 urls锛夛紴 `HTTP_PORTS`/`HTTPS_PORTS`
    ///   · Kestrel 端点存在时 urls 与 HTTP_PORTS 均不生效；urls 存在时 HTTP_PORTS 不生效；
    ///   · HTTP_PORTS 只承载端口，**恒为 0.0.0.0 通配明文监听**（旧实现未枚举它）：
    ///     仅设 ASPNETCORE_HTTP_PORTS 即可让生产环境以明文远程监听启动（探针复现）；
    ///   · 缺 scheme 的 `--urls 0.0.0.0:5080` 会被 Kestrel 直接拒绝启动（无监听，无需本守卫）。
    ///
    /// 策略（fail-closed）：**任一来源**——包括被更高优先级来源压住、当前未生效的冲突来源——
    /// 出现未豁免的远程明文 HTTP 即拒绝。配置冲突属于必须显式清理的交付错误，本守卫不把安全性
    /// 押在"推断哪个来源生效"上（推断依赖框架版本行为，冲突配置本身也说明现场配置失控）。
    /// HTTPS 端点/端口是 TLS，始终合规；unix socket 等非 HTTP 监听跳过。
    /// </summary>
    /// <summary>来源枚举的实现体；internal 以便回归测试直接覆盖各来源组合（见 ListenBoundaryTests）。</summary>
    internal static IReadOnlyList<string> InsecurePlaintextListenSources(IConfiguration configuration)
    {
        var insecure = new List<string>();
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url) && IsInsecurePlaintextHttp(url))
                insecure.Add($"Kestrel:Endpoints:{endpoint.Key}:Url={url}");
        }

        var urls = new List<string>();
        foreach (var key in new[] { "ASPNETCORE_URLS", "urls" })
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                urls.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var url in urls.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (IsInsecurePlaintextHttp(url)) insecure.Add($"urls={url}");
        }

        // HTTP_PORTS / ASPNETCORE_HTTP_PORTS：端口列表无法表达 loopback（恒为通配明文）。
        foreach (var key in new[] { "HTTP_PORTS", "ASPNETCORE_HTTP_PORTS" })
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value)) insecure.Add($"{key}={value} (binds 0.0.0.0 plaintext)");
        }
        // HTTPS_PORTS / ASPNETCORE_HTTPS_PORTS 是 TLS 端点，合规；不列入拒绝来源。
        return insecure;
    }

    /// <summary>
    /// 判定单个监听 URL 是否属"未豁免的远程明文"。
    /// 通配形式（* / + / 0.0.0.0 / [::] / 仅端口）无法用 Uri.TryCreate 解析，必须显式识别为
    /// "非 loopback"（此前这些形式被静默跳过，等于放过了真正的远程明文监听入口）。
    /// </summary>
    private static bool IsInsecurePlaintextHttp(string raw)
    {
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false; // TLS 端点合规
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false; // unix socket / 无 scheme（Kestrel 拒绝启动）
        var authority = raw["http://".Length..].TrimEnd('/');
        var host = authority.Split('/', 2)[0];
        if (host.StartsWith('['))
        {
            var bracketEnd = host.IndexOf(']');
            host = bracketEnd > 0 ? host[1..bracketEnd] : host;
        }
        else
        {
            host = host.Split(':', 2)[0];
        }
        if (host.Length == 0) host = "*"; // http://:5080 → 所有网卡
        return !IsLoopbackHost(host);
    }

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
        // R3-02：来源枚举覆盖 urls / Kestrel 端点 / HTTP_PORTS（含冲突来源，fail-closed）。
        if (environment.IsProduction() && securityOptions.Enabled && !securityOptions.AllowInsecureRemoteTransport)
        {
            var insecure = InsecurePlaintextListenSources(configuration);
            if (insecure.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to start: plaintext HTTP is configured for a non-loopback address ({string.Join("; ", insecure)}). " +
                    "Account passwords and session cookies would travel unprotected (a Secure cookie cannot be set over HTTP). " +
                    "Bind to loopback (http://127.0.0.1:5080), terminate TLS in front of the host (HTTPS endpoint / trusted reverse proxy), " +
                    "or set Security:AllowInsecureRemoteTransport=true for an isolated test network. " +
                    "Remove conflicting sources instead of relying on which one would win.");
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
        // 结果落库：output.database 节点通过 IResultRecordStore 写 result_records 表（migration 23）。
        services.AddSingleton<IResultRecordStore, SqliteResultRecordStore>();

        // 跨轮计数：value.counter 通过 IRunCounterStore 记住 total/pass/fail。
        // 单例是硬要求，否则每次解析执行器都会得到一份新状态，"连续 NG"永远停在 1。
        // 2026-10-29：进程内状态由 RunCounterPersistence 每 5s 落盘到 data/runtime/counters.json，
        // 并在启动时恢复；节点侧不再有 static 兜底（没注册就显式报错）。
        services.AddSingleton<InMemoryRunCounterStore>();
        services.AddSingleton<IRunCounterStore>(provider => provider.GetRequiredService<InMemoryRunCounterStore>());
        services.AddSingleton<LegacyStorageMigrationService>();
        // F03：失败锁持久化到数据根——重启后仍保留，直到受控恢复入口（或人工）完成核对后解除；
        // 纯内存的旧实现重启即丢锁，半恢复的主机会在多实例资产不一致的状态下放行写入。
        // Q08：同一目录下另存两个标记——restore-transaction.json（恢复中途被终止 → 启动即锁）；
        // 与 restart-pending.json（恢复成功但未重启 → 业务 503 直到重启）。
        // Q01（2026-10-13 审查）：同时识别旧版离线 CLI 的 .visionstudio-restore-intent.json
        // （现场可能残留；与 restore-transaction.json 同一判定语义），并登记跨进程维护活跃标记
        // maintenance-active.json锛圦04锛氫笌绂荤嚎 CLI 鐨勭淮鎶や簰鏂ワ級銆?
        var dataRoot = VisionStudioDataRoot.Resolve(environment.ContentRootPath);
        var maintenanceDir = Path.Combine(dataRoot, ".storage-maintenance");
        services.AddSingleton(sp => new StorageMaintenanceCoordinator(
            Path.Combine(maintenanceDir, "failure-lock.json"),
            sp.GetRequiredService<ILogger<StorageMaintenanceCoordinator>>(),
            Path.Combine(maintenanceDir, "restart-pending.json"),
            Path.Combine(maintenanceDir, "restore-transaction.json"),
            Path.Combine(dataRoot, ".visionstudio-restore-intent.json"),
            Path.Combine(maintenanceDir, "maintenance-active.json")));
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

        // 离线回放读盘授权：默认可读数据根与媒体库根，现场如需额外根目录用 VisionStudio:ReplayInputRoots 显式放开。
        services.AddSingleton<IReplayInputResolver>(provider =>
            new ConfiguredReplayInputResolver(configuration, provider.GetRequiredService<IWebHostEnvironment>()));

        services.AddBuiltInNodeExecutors();
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
        // 通用循环（flow.while / flow.repeat / flow.break）的迭代步：由编译器生成，需可被 DI 解析。
        services.AddTransient<VisionLoopStep>();
        // R10：统一运行边界的原始异常日志观察者——用户侧结果只保留简洁诊断，日志保留完整异常链。
        services.AddSingleton<IVisionRunExceptionObserver, VisionRunExceptionLogObserver>();
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
        // A02：设备/机器人/相机的真实命令派发边界共用租约注册表作为所有权闸门——
        // 失去所有权（换代/撤销）的旧执行在派发前被拒绝。惰性解析打破
        // "绠＄悊鍣?鈫?闂搁棬 鈫?娉ㄥ唽琛?鈫?娲诲姩鎺㈤拡 鈫?绠＄悊鍣?鐨勬瀯閫犵幆銆?
        services.AddSingleton<IDeviceCommandOwnershipGate>(sp =>
            new LazyDeviceCommandOwnershipGate(() => sp.GetRequiredService<DeviceLeaseRegistry>()));

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
        services.AddSingleton<ModelPackageService>();
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
        services.AddSingleton<CameraIntrinsicAssetStore>();
        services.AddSingleton<CalibrationWizardService>();
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

        // Start order matters: migrate persisted metadata first, then retire runs left by a previous
        // process, then register runtime assets/plugins, start Diagnostics observation, then allow
        // Production AutoStart. Trace retention starts after the runtime is initialized.
        services.AddSingleton<InterruptedRunRecovery>();
        services.AddHostedService<StorageMigrationHostedService>();
        services.AddHostedService<InterruptedRunRecoveryHostedService>();
        services.AddHostedService<RuntimeBootstrapHostedService>();
        services.AddHostedService<VendorProvenanceBootstrapHostedService>();
        services.AddHostedService<DiagnosticsCenterService>(sp => sp.GetRequiredService<DiagnosticsCenterService>());
        services.AddHostedService<ProductionRuntimeHostedService>();
        services.AddHostedService<TraceRetentionHostedService>();
        services.AddHostedService<StorageCapacityHostedService>();
        // 跨轮计数落盘：启动即恢复，之后每 5s 有变化才写（停机补写一次）。
        services.AddHostedService<RunCounterPersistence>();

        return services;
    }

}

/// <summary>
/// R10（2026-10-12 复审）：宿主侧的运行异常日志观察者。节点异常在结果路径上只保留简短消息，
/// 这里以结构化日志记录**原始异常**（完整堆栈 / InnerException）——同一失败只在此记录一次。
/// </summary>
internal sealed class VisionRunExceptionLogObserver(ILogger<VisionRunExceptionLogObserver> logger) : IVisionRunExceptionObserver
{
    public void OnRunException(VisionRunExceptionContext context, Exception exception)
        => logger.LogError(exception,
            "Workflow run {RunId} (workflow {WorkflowId} '{WorkflowName}', node {NodeId}) failed with an unhandled exception; the user-facing result carries only the concise message, the full chain is preserved here.",
            context.RunId, context.WorkflowId, context.WorkflowName, context.NodeId);
}
