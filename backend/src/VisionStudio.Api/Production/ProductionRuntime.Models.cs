using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public enum ProductionRuntimeState
{
    Stopped,
    Starting,
    Running,
    Recovering,
    Stopping,
    Faulted
}

public sealed record ProductionRuntimeConfig(
    string? JobId = null,
    bool AutoStart = false,
    int CycleDelayMs = 25,
    int MaxCycleMs = 15000,
    int MaxConsecutiveFailures = 3,
    bool AutoRecover = true,
    int RecoveryDelayMs = 1000,
    int StopTimeoutMs = 5000,
    bool PtpDriftGuardEnabled = true,
    int PtpGuardWindowRuns = 20,
    int PtpGuardMinimumEvidenceRuns = 5,
    double PtpGuardMinimumReadyRate = 0.99,
    int PtpGuardCheckEveryCycles = 10,
    bool PtpGuardFaultOnMasterClockChange = true,
    bool SynchronizationHealthGuardEnabled = true,
    int SynchronizationGuardWindowRuns = 20,
    int SynchronizationGuardMinimumEvidenceRuns = 5,
    double SynchronizationGuardMaximumFailureRate = 0.05,
    double SynchronizationGuardMaximumFrameTimeoutRate = 0.05,
    int SynchronizationGuardMaxConsecutiveFailures = 2,
    int SynchronizationGuardMaxConsecutiveSkewViolations = 3,
    double SynchronizationGuardMaximumNativeFrameLossRate = 0.005,
    long SynchronizationGuardMaximumBufferUnderruns = 0,
    long SynchronizationGuardMaximumResynchronizations = 0,
    double SynchronizationGuardMaximumSequenceGapRate = 0.01,
    int SynchronizationGuardCheckEveryCycles = 5,
    int SynchronizationGuardUnhealthyChecksToFault = 2,
    int SynchronizationGuardHealthyChecksToRecover = 3,
    int SynchronizationGuardRecoveryTimeoutMs = 30000,
    /// <summary>同一故障事件内允许的自动恢复总次数：耗尽后锁定 Faulted，需人工核对与恢复。</summary>
    int MaxRecoveryAttempts = 3);

public sealed record ProductionRuntimeStatus(
    ProductionRuntimeState State,
    string? JobId,
    int? LockedJobVersion,
    string? LockedWorkflowHash,
    string? LockedDependencyManifestHash,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastCycleAt,
    string? CurrentRunId,
    long CycleCount,
    long OkCount,
    long NgCount,
    long ErrorCount,
    int ConsecutiveFailures,
    long WatchdogTrips,
    double LastDurationMs,
    string? LastDisposition,
    string? LastError,
    bool ProductionLocked,
    ProductionPtpGuardSnapshot? PtpGuard,
    ProductionSynchronizationGuardSnapshot? SynchronizationGuard,
    int PendingTraceCount = 0,
    int TraceQueueCapacity = 8,
    /// <summary>当前故障事件内已消耗的自动恢复次数（成功后清零）。</summary>
    int RecoveryAttempts = 0,
    /// <summary>锁定快照的工作流是否引用设备（PLC）/机器人：含设备副作用的失败不允许自动重跑。</summary>
    bool HasDeviceSideEffects = false);

public sealed record ProductionStartRequest(string? JobId = null);
public sealed record DeviceActionResolutionRequest(
    string RunId,
    string ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    string Reason,
    string Evidence,
    string DeviceIdentityEvidence,
    /// <summary>F03：要核对的手动/临时运行动作意图（runId）——与生产意图二选一。</summary>
    string? ManualIntentId = null,
    /// <summary>R02（2026-10-11 复审）：生产核对同样必须指认原相机清单（与耐久安全状态精确匹配）。</summary>
    IReadOnlyList<string>? CameraIds = null);
public sealed record ProductionDeviceActionPendingState(
    string? RunId,
    string? ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyDictionary<string, string> DeviceFingerprints,
    DeviceActionSafetyPhase Phase,
    DateTimeOffset? SetAt,
    string? Reason,
    bool Unreadable,
    /// <summary>R02：动作时锁定的相机清单（旧记录缺省为空）。</summary>
    IReadOnlyList<string>? CameraIds = null);

public sealed class ProductionRuntimeConfigStore
{
    /// <summary>
    /// R3-05（2026-10-13 R3 审查）：自动恢复预算的上限。0 = 不自动恢复（失败立即 Faulted 等人工），
    /// 上限 100——配合 RecoveryDelayMs 的最大值仍让"有限重试"的等待时长可控；负数与极大值是
    /// 未定义语义（负数立即耗尽预算、极大值让约束失效），保存与读取都必须收敛到 [0, 100]。
    /// </summary>
    public const int MaxRecoveryAttemptsLimit = 100;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProductionRuntimeConfigStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "runtime.json");
    }

    public async Task<ProductionRuntimeConfig> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new ProductionRuntimeConfig();
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var config = JsonSerializer.Deserialize<ProductionRuntimeConfig>(document.RootElement.GetRawText(), _json) ?? new ProductionRuntimeConfig();
            var defaults = new ProductionRuntimeConfig();
            if (!HasProperty(document.RootElement, "ptpDriftGuardEnabled"))
            {
                config = config with
                {
                    PtpDriftGuardEnabled = defaults.PtpDriftGuardEnabled,
                    PtpGuardWindowRuns = defaults.PtpGuardWindowRuns,
                    PtpGuardMinimumEvidenceRuns = defaults.PtpGuardMinimumEvidenceRuns,
                    PtpGuardMinimumReadyRate = defaults.PtpGuardMinimumReadyRate,
                    PtpGuardCheckEveryCycles = defaults.PtpGuardCheckEveryCycles,
                    PtpGuardFaultOnMasterClockChange = defaults.PtpGuardFaultOnMasterClockChange
                };
            }
            if (!HasProperty(document.RootElement, "synchronizationHealthGuardEnabled"))
            {
                config = config with
                {
                    SynchronizationHealthGuardEnabled = defaults.SynchronizationHealthGuardEnabled,
                    SynchronizationGuardWindowRuns = defaults.SynchronizationGuardWindowRuns,
                    SynchronizationGuardMinimumEvidenceRuns = defaults.SynchronizationGuardMinimumEvidenceRuns,
                    SynchronizationGuardMaximumFailureRate = defaults.SynchronizationGuardMaximumFailureRate,
                    SynchronizationGuardMaximumFrameTimeoutRate = defaults.SynchronizationGuardMaximumFrameTimeoutRate,
                    SynchronizationGuardMaxConsecutiveFailures = defaults.SynchronizationGuardMaxConsecutiveFailures,
                    SynchronizationGuardMaxConsecutiveSkewViolations = defaults.SynchronizationGuardMaxConsecutiveSkewViolations,
                    SynchronizationGuardMaximumNativeFrameLossRate = defaults.SynchronizationGuardMaximumNativeFrameLossRate,
                    SynchronizationGuardMaximumBufferUnderruns = defaults.SynchronizationGuardMaximumBufferUnderruns,
                    SynchronizationGuardMaximumResynchronizations = defaults.SynchronizationGuardMaximumResynchronizations,
                    SynchronizationGuardMaximumSequenceGapRate = defaults.SynchronizationGuardMaximumSequenceGapRate,
                    SynchronizationGuardCheckEveryCycles = defaults.SynchronizationGuardCheckEveryCycles,
                    SynchronizationGuardUnhealthyChecksToFault = defaults.SynchronizationGuardUnhealthyChecksToFault,
                    SynchronizationGuardHealthyChecksToRecover = defaults.SynchronizationGuardHealthyChecksToRecover,
                    SynchronizationGuardRecoveryTimeoutMs = defaults.SynchronizationGuardRecoveryTimeoutMs
                };
            }
            else if (!HasProperty(document.RootElement, "synchronizationGuardMaximumNativeFrameLossRate"))
            {
                // V0.44 already has the synchronization guard but predates vendor-native transport thresholds.
                // Apply V0.45 defaults explicitly instead of depending on constructor missing-parameter behavior.
                config = config with
                {
                    SynchronizationGuardMaximumNativeFrameLossRate = defaults.SynchronizationGuardMaximumNativeFrameLossRate,
                    SynchronizationGuardMaximumBufferUnderruns = defaults.SynchronizationGuardMaximumBufferUnderruns,
                    SynchronizationGuardMaximumResynchronizations = defaults.SynchronizationGuardMaximumResynchronizations
                };
            }
            // R3-05：旧文件可能带着越界的恢复预算（负数立即耗尽预算 / 极大值让"有限重试"失效）——
            // 读取时收敛到合法区间（保存路径严格校验并 400），让运行语义始终受约束。
            if (config.MaxRecoveryAttempts < 0 || config.MaxRecoveryAttempts > MaxRecoveryAttemptsLimit)
                config = config with { MaxRecoveryAttempts = Math.Clamp(config.MaxRecoveryAttempts, 0, MaxRecoveryAttemptsLimit) };
            return config;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProductionRuntimeConfig> SaveAsync(ProductionRuntimeConfig config, CancellationToken ct)
    {
        Validate(config);
        await _gate.WaitAsync(ct);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, config, _json, ct);
            File.Move(temp, _path, true);
            return config;
        }
        finally { _gate.Release(); }
    }

    private static bool HasProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Validate(ProductionRuntimeConfig config)
    {
        if (config.CycleDelayMs < 0 || config.CycleDelayMs > 60000) throw new ApiValidationException("CycleDelayMs must be 0..60000.");
        if (config.MaxCycleMs < 100 || config.MaxCycleMs > 600000) throw new ApiValidationException("MaxCycleMs must be 100..600000.");
        if (config.MaxConsecutiveFailures < 1 || config.MaxConsecutiveFailures > 1000) throw new ApiValidationException("MaxConsecutiveFailures must be 1..1000.");
        // R3-05（2026-10-13 R3 审查）：自动恢复预算此前遗漏范围校验——负数立即耗尽预算、
        // 极大值让"有限重试"失去实际意义（探针实测 -1 与 int.MaxValue 均被 200 保存）。
        // 0 = 不自动恢复（失败立即 Faulted）；上限见 MaxRecoveryAttemptsLimit。
        if (config.MaxRecoveryAttempts < 0 || config.MaxRecoveryAttempts > MaxRecoveryAttemptsLimit)
            throw new ApiValidationException($"MaxRecoveryAttempts must be 0..{MaxRecoveryAttemptsLimit} (0 disables automatic recovery; failures lock Faulted for manual recovery).");
        if (config.RecoveryDelayMs < 0 || config.RecoveryDelayMs > 600000) throw new ApiValidationException("RecoveryDelayMs must be 0..600000.");
        if (config.StopTimeoutMs < 100 || config.StopTimeoutMs > 60000) throw new ApiValidationException("StopTimeoutMs must be 100..60000.");
        if (config.PtpGuardWindowRuns < 5 || config.PtpGuardWindowRuns > 500) throw new ApiValidationException("PtpGuardWindowRuns must be 5..500.");
        if (config.PtpGuardMinimumEvidenceRuns < 1 || config.PtpGuardMinimumEvidenceRuns > config.PtpGuardWindowRuns) throw new ApiValidationException("PtpGuardMinimumEvidenceRuns must be 1..PtpGuardWindowRuns.");
        if (config.PtpGuardMinimumReadyRate < 0.5 || config.PtpGuardMinimumReadyRate > 1.0) throw new ApiValidationException("PtpGuardMinimumReadyRate must be 0.5..1.0.");
        if (config.PtpGuardCheckEveryCycles < 1 || config.PtpGuardCheckEveryCycles > 10000) throw new ApiValidationException("PtpGuardCheckEveryCycles must be 1..10000.");
        if (config.SynchronizationGuardWindowRuns < 5 || config.SynchronizationGuardWindowRuns > 500) throw new ApiValidationException("SynchronizationGuardWindowRuns must be 5..500.");
        if (config.SynchronizationGuardMinimumEvidenceRuns < 1 || config.SynchronizationGuardMinimumEvidenceRuns > config.SynchronizationGuardWindowRuns) throw new ApiValidationException("SynchronizationGuardMinimumEvidenceRuns must be 1..SynchronizationGuardWindowRuns.");
        if (config.SynchronizationGuardMaximumFailureRate < 0 || config.SynchronizationGuardMaximumFailureRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumFailureRate must be 0..0.5.");
        if (config.SynchronizationGuardMaximumFrameTimeoutRate < 0 || config.SynchronizationGuardMaximumFrameTimeoutRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumFrameTimeoutRate must be 0..0.5.");
        if (config.SynchronizationGuardMaxConsecutiveFailures < 1 || config.SynchronizationGuardMaxConsecutiveFailures > 100) throw new ApiValidationException("SynchronizationGuardMaxConsecutiveFailures must be 1..100.");
        if (config.SynchronizationGuardMaxConsecutiveSkewViolations < 1 || config.SynchronizationGuardMaxConsecutiveSkewViolations > 100) throw new ApiValidationException("SynchronizationGuardMaxConsecutiveSkewViolations must be 1..100.");
        if (config.SynchronizationGuardMaximumNativeFrameLossRate < 0 || config.SynchronizationGuardMaximumNativeFrameLossRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumNativeFrameLossRate must be 0..0.5.");
        if (config.SynchronizationGuardMaximumBufferUnderruns < 0 || config.SynchronizationGuardMaximumBufferUnderruns > 1_000_000) throw new ApiValidationException("SynchronizationGuardMaximumBufferUnderruns must be 0..1000000.");
        if (config.SynchronizationGuardMaximumResynchronizations < 0 || config.SynchronizationGuardMaximumResynchronizations > 1_000_000) throw new ApiValidationException("SynchronizationGuardMaximumResynchronizations must be 0..1000000.");
        if (config.SynchronizationGuardMaximumSequenceGapRate < 0 || config.SynchronizationGuardMaximumSequenceGapRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumSequenceGapRate must be 0..0.5.");
        if (config.SynchronizationGuardCheckEveryCycles < 1 || config.SynchronizationGuardCheckEveryCycles > 10000) throw new ApiValidationException("SynchronizationGuardCheckEveryCycles must be 1..10000.");
        if (config.SynchronizationGuardUnhealthyChecksToFault < 1 || config.SynchronizationGuardUnhealthyChecksToFault > 100) throw new ApiValidationException("SynchronizationGuardUnhealthyChecksToFault must be 1..100.");
        if (config.SynchronizationGuardHealthyChecksToRecover < 1 || config.SynchronizationGuardHealthyChecksToRecover > 100) throw new ApiValidationException("SynchronizationGuardHealthyChecksToRecover must be 1..100.");
        if (config.SynchronizationGuardRecoveryTimeoutMs < 1000 || config.SynchronizationGuardRecoveryTimeoutMs > 600000) throw new ApiValidationException("SynchronizationGuardRecoveryTimeoutMs must be 1000..600000.");
    }
}

