namespace VisionStudio.Api;

/// <summary>
/// Shared process-pool, shared-memory, and Windows Job Object options common to both
/// PluginWorkerOptions and ScriptWorkerOptions.
/// </summary>
public abstract class WorkerPoolOptionsBase
{
    public int StartupTimeoutMs { get; set; } = 10_000;
    public int ExecutionTimeoutMs { get; set; } = 30_000;
    public int MaxWorkingSetMb { get; set; } = 2048;
    public int MaxRestartsPerMinute { get; set; } = 5;
    public string? WorkerAssemblyPath { get; set; }
    public bool SharedMemoryEnabled { get; set; } = true;
    public int SharedMemoryThresholdBytes { get; set; } = 1_048_576;
    public string SharedMemoryRootPath { get; set; } = string.Empty;
    public int SharedMemoryStaleMinutes { get; set; } = 30;
    /// <summary>Windows Job Object 进程私有内存上限；0 = 不启用。</summary>
    public int JobObjectMemoryLimitMb { get; set; }
    /// <summary>Windows Job Object CPU 硬上限（百分比）；0 = 不启用。</summary>
    public int JobObjectCpuRatePercent { get; set; }

    internal string ResolveWorkerAssemblyPath(string contentRootPath, string defaultAssemblyFileName)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(WorkerAssemblyPath)
            ? Path.Combine(AppContext.BaseDirectory, defaultAssemblyFileName)
            : Path.IsPathRooted(WorkerAssemblyPath)
                ? WorkerAssemblyPath
                : Path.Combine(contentRootPath, WorkerAssemblyPath));

    internal string ResolveSharedMemoryRoot(string contentRootPath)
        => Path.GetFullPath(Path.IsPathRooted(SharedMemoryRootPath)
            ? SharedMemoryRootPath
            : Path.Combine(contentRootPath, SharedMemoryRootPath));
}
