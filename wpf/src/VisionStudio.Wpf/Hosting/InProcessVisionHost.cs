using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCvSharp;
using Serilog;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Diagnostics;
using VisionStudio.Api.Hosting;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Api.Security;
using VisionStudio.Api.Spc;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Wpf.Hosting;

/// <summary>
/// 方案 C 进程内视觉宿主：
/// 在 WPF 进程内直接构建并启动与 <c>VisionStudio.Api</c> 完全一致的后端服务图，
/// 不监听 HTTP 端口，也不经过 REST/SignalR 序列化，工作流执行与相机帧直接内存交互。
/// </summary>
public sealed class InProcessVisionHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Serilog.Core.Logger _serilogLogger;
    private readonly FileStream _dataRootLock;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    private InProcessVisionHost(
        IHost host,
        Serilog.Core.Logger serilogLogger,
        WpfLogSink logSink,
        DesktopWebHostEnvironment environment,
        string dataRoot,
        FileStream dataRootLock)
    {
        _host = host;
        _serilogLogger = serilogLogger;
        LogSink = logSink;
        Environment = environment;
        DataRoot = dataRoot;
        _dataRootLock = dataRootLock;
    }

    public WpfLogSink LogSink { get; }
    public DesktopWebHostEnvironment Environment { get; }
    public string DataRoot { get; }
    public IServiceProvider Services => _host.Services;

    // 常用核心服务直通访问器（均由进程内单例容器解析）
    public VisionNodeRegistry NodeRegistry => Services.GetRequiredService<VisionNodeRegistry>();
    public WorkflowStore WorkflowStore => Services.GetRequiredService<WorkflowStore>();
    public VisionWorkflowCompiler Compiler => Services.GetRequiredService<VisionWorkflowCompiler>();
    public WorkflowModuleExpander ModuleExpander => Services.GetRequiredService<WorkflowModuleExpander>();
    public IVisionWorkflowRunner Runner => Services.GetRequiredService<IVisionWorkflowRunner>();
    public DebugSessionService DebugSessions => Services.GetRequiredService<DebugSessionService>();
    public ProductionRuntimeService Production => Services.GetRequiredService<ProductionRuntimeService>();
    public JobStore Jobs => Services.GetRequiredService<JobStore>();
    public ProductRecipeStore Recipes => Services.GetRequiredService<ProductRecipeStore>();
    public AlarmStore Alarms => Services.GetRequiredService<AlarmStore>();
    public CameraManager Cameras => Services.GetRequiredService<CameraManager>();
    public DeviceManager Devices => Services.GetRequiredService<DeviceManager>();
    public RobotManager Robots => Services.GetRequiredService<RobotManager>();
    public MonitorSnapshotReader MonitorReader => Services.GetRequiredService<MonitorSnapshotReader>();
    public RunTraceRecorder TraceRecorder => Services.GetRequiredService<RunTraceRecorder>();
    public TraceabilityStore Traces => Services.GetRequiredService<TraceabilityStore>();
    public DiagnosticsCenterService Diagnostics => Services.GetRequiredService<DiagnosticsCenterService>();
    public NodeDurationStatistics NodeDurations => Services.GetRequiredService<NodeDurationStatistics>();
    public NodeSpanBuffer NodeSpans => Services.GetRequiredService<NodeSpanBuffer>();
    public DatasetValidationStore ValidationDatasets => Services.GetRequiredService<DatasetValidationStore>();
    public DatasetValidationService ValidationRunner => Services.GetRequiredService<DatasetValidationService>();
    public SpcSeriesReader SpcReader => Services.GetRequiredService<SpcSeriesReader>();
    public WorkflowModuleStore ModuleStore => Services.GetRequiredService<WorkflowModuleStore>();
    public MediaLibraryService Media => Services.GetRequiredService<MediaLibraryService>();
    public AuditEventStore AuditEvents => Services.GetRequiredService<AuditEventStore>();
    public RuntimeDependencyManifestService Dependencies => Services.GetRequiredService<RuntimeDependencyManifestService>();
    public DeviceActionAuthorizationService ActionAuth => Services.GetRequiredService<DeviceActionAuthorizationService>();
    public string SamplesDirectory => DesktopWebHostEnvironment.ResolveSamplesDirectory(Environment.ContentRootPath);

    public void ReportError(Exception exception, string operation) =>
        _serilogLogger.Error(exception, "桌面操作失败: {Operation}", operation);

    public async Task StopProductionAsync(CancellationToken cancellationToken = default)
    {
        foreach (var station in Production.ListStatuses())
        {
            var stopped = await Production.StopAsync(station.StationId, cancellationToken);
            if (stopped.State != ProductionRuntimeState.Stopped)
            {
                throw new InvalidOperationException($"工位 {station.StationId} 未停止：{stopped.LastError}");
            }
        }
    }

    /// <summary>
    /// 启动进程内视觉引擎宿主，初始化数据库迁移、相机/设备/机器人适配器与内置节点目录。
    /// </summary>
    public static async Task<InProcessVisionHost> StartNewAsync(
        string? customDataRoot = null,
        string? customContentRoot = null,
        string environmentName = "Development",
        CancellationToken cancellationToken = default)
    {
        var contentRoot = customContentRoot ?? DesktopWebHostEnvironment.ResolveDefaultContentRoot();
        var dataRoot = string.IsNullOrWhiteSpace(customDataRoot)
            ? DesktopWebHostEnvironment.ResolveDefaultDataRoot(contentRoot)
            : Path.GetFullPath(customDataRoot);
        var packagePath = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dataPath = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(packagePath, "build-info.json"))
            && (dataPath.Equals(packagePath, StringComparison.OrdinalIgnoreCase)
                || dataPath.StartsWith(packagePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || packagePath.StartsWith(dataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("发布版的数据根必须位于版本目录之外，且不能包含版本目录。");
        }
        Directory.CreateDirectory(dataRoot);

        var logSink = new WpfLogSink();
        var logFilePattern = Path.Combine(dataRoot, "logs", "wpf-.log");
        FileStream? dataRootLock = null;
        Serilog.Core.Logger? serilogLogger = null;
        HostApplicationBuilder? builder = null;
        IHost? host = null;
        try
        {
            try
            {
                dataRootLock = new FileStream(Path.Combine(dataRoot, ".wpf.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new IOException($"无法独占桌面数据根 '{dataRoot}'。请关闭使用该目录的其他桌面实例并检查文件权限。", exception);
            }
            serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Sink(logSink)
                .WriteTo.File(
                    path: logFilePattern,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
            var desktopEnv = new DesktopWebHostEnvironment(contentRoot, environmentName);
            builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = "VisionStudio.Wpf",
                EnvironmentName = environmentName,
                ContentRootPath = contentRoot
            });

            // 注入基础 appsettings.json（若存在于输出目录）及桌面端数据根覆盖
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                builder.Configuration.AddJsonFile(appSettingsPath, optional: true, reloadOnChange: false);
            }
            builder.Configuration.AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.Wpf.json"), optional: false, reloadOnChange: false);

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [VisionStudioDataRoot.ConfigurationKey] = dataRoot,
                // 桌面本地模式允许访问已授权目录；动态分配模拟机器人端口，避免与同时运行的 Web API (40501) 冲突
                ["Security:Enabled"] = "true",
                ["Security:AutoLoginAdmin"] = "true",
                ["RobotTcpSimulator:Port"] = "0"
            });

            builder.Services.AddLogging(logging =>
            {
                logging.AddSerilog(serilogLogger, dispose: false);
            });

            builder.Services.AddSingleton(logSink);
            builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(desktopEnv);
            builder.Services.AddVisionStudioHost(builder.Configuration, desktopEnv);

            host = builder.Build();
            serilogLogger.Information(
                "正在启动 VisionStudio WPF 进程内宿主: 环境={Env}, 内容根={ContentRoot}, 数据根={DataRoot}",
                environmentName, contentRoot, dataRoot);

            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            var desktopHost = new InProcessVisionHost(host, serilogLogger, logSink, desktopEnv, dataRoot, dataRootLock);
            var frames = Path.Combine(desktopHost.SamplesDirectory, "camera_frames");
            if (Directory.Exists(frames) && !desktopHost.Cameras.List().Any(camera => camera.Id == "file-demo"))
            {
                var media = host.Services.GetRequiredService<MediaLibraryService>();
                media.SeedCollectionFromDirectory("bundled-demo", frames);
                var source = media.ResolveSource("media://bundled-demo");
                desktopHost.Cameras.Register(new FileCameraDevice("file-demo", "Bundled File Camera", source.Source, source.PhysicalPath));
            }
            return desktopHost;
        }
        catch (Exception exception)
        {
            serilogLogger?.Error(exception, "启动桌面宿主失败");
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (host is not null) await host.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (builder?.Services.FirstOrDefault(service => service.ServiceType == typeof(RollingFileLoggerProvider))
                        ?.ImplementationInstance is RollingFileLoggerProvider fileLogger)
                        fileLogger.Dispose();
                    if (host is not null)
                    {
                        var database = host.Services.GetService<SqliteMetadataDatabase>();
                        if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
                        else host.Dispose();
                        database?.ReleaseIdleConnections();
                    }
                }
                finally
                {
                    try
                    {
                        if (serilogLogger is not null) await serilogLogger.DisposeAsync().ConfigureAwait(false);
                    }
                    finally { dataRootLock?.Dispose(); }
                }
            }
            throw;
        }
    }

    /// <summary>
    /// 在进程内直接编译校验工作流（含模块展开）。
    /// </summary>
    public async Task<CompiledWorkflow> CompileAndValidateAsync(
        WorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        var expanded = await ModuleExpander.ExpandAsync(workflow, cancellationToken).ConfigureAwait(false);
        return Compiler.Compile(expanded.Workflow);
    }

    /// <summary>
    /// 在进程内直接运行工作流：持有硬件租约与命令所有权、记录追溯证据，并实时回传逐节点进度。
    /// </summary>
    public async Task<WorkflowRunResult> RunWorkflowAsync(
        WorkflowDefinition workflow,
        VisionRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var runId = RunTraceRecorder.NewRunId();
        var context = new RunTraceContext("WpfDesktop");

        using var lease = await Production.AcquireRunLeaseAsync(
            runId, workflow, $"无法运行工作流 '{workflow.Id}'", cancellationToken).ConfigureAwait(false);
        using var commandScope = DeviceCommandOwnership.Enter(lease);

        var effectiveWorkflow = (await ModuleExpander.ExpandAsync(workflow, cancellationToken).ConfigureAwait(false)).Workflow;
        await Dependencies.ValidateCalibrationBindingsAsync(effectiveWorkflow, cancellationToken).ConfigureAwait(false);

        var hasSideEffects = DeviceSideEffectClassifier.HasDeviceSideEffects(effectiveWorkflow, NodeRegistry);
        using var actionIntent = hasSideEffects
            ? await ActionAuth.BeginWorkflowIntentAsync("wpf.workflow.run", effectiveWorkflow, runId, cancellationToken).ConfigureAwait(false)
            : null;

        if (hasSideEffects)
        {
            await TraceRecorder.BeginStrictAsync(runId, DateTimeOffset.UtcNow, workflow, context).ConfigureAwait(false);
        }
        else
        {
            await TraceRecorder.BeginAsync(runId, DateTimeOffset.UtcNow, workflow, context).ConfigureAwait(false);
        }

        var effectiveOptions = (options ?? new VisionRunOptions()) with
        {
            CaptureNodeImages = true
        };

        var result = await Runner.RunAsync(workflow, effectiveOptions, runId, cancellationToken).ConfigureAwait(false);
        await TraceRecorder.FinalizeAsync(result, workflow, context).ConfigureAwait(false);

        if (actionIntent is not null)
        {
            if (result.Success && !DeviceSideEffectClassifier.RequiresManualReconciliation(effectiveWorkflow, result, NodeRegistry))
            {
                actionIntent.Complete();
            }
            else if (result.Success)
            {
                actionIntent.MarkUnknown("流程返回成功，但已受理的异步设备动作仍等待完成确认");
            }
        }

        _serilogLogger.Information(
            "工作流执行结束: RunId={RunId}, Success={Success}, DurationMs={Duration:F2}, Disposition={Disposition}",
            result.RunId, result.Success, result.TotalDurationMs, result.QualityDisposition ?? "-");

        return result;
    }

    /// <summary>
    /// 直接从相机环形缓冲租借最新帧并深拷贝一份 <see cref="Mat"/> 供 WPF 内存渲染（零 JPEG/HTTP 编解码）。
    /// </summary>
    public async Task<(Mat Frame, long Sequence, DateTimeOffset Timestamp)> GrabCameraMatAsync(
        string cameraId,
        bool triggerBeforeGrab = false,
        CancellationToken cancellationToken = default)
    {
        using var lease = await Cameras.AcquireAsync(
            cameraId,
            CameraFrameMode.Latest,
            TimeSpan.FromSeconds(3),
            autoStart: true,
            triggerBeforeGrab: triggerBeforeGrab,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (lease.Image.Clone(), lease.Sequence, lease.Timestamp);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
                _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _host.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _serilogLogger.Error(ex, "停止 WPF 进程内视觉宿主失败，保留宿主以便重试");
            throw;
        }
        // AddVisionStudioHost registers this pre-created provider; DI does not own its file handle.
        var database = Services.GetRequiredService<SqliteMetadataDatabase>();
        Services.GetRequiredService<RollingFileLoggerProvider>().Dispose();
        if (_host is IAsyncDisposable asyncHost)
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        else
            _host.Dispose();
        database.ReleaseIdleConnections();
        await _serilogLogger.DisposeAsync().ConfigureAwait(false);
        _dataRootLock.Dispose();
    }
}
