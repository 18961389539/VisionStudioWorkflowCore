using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisionStudio.Api.Workers;

/// <summary>
/// Windows Job Object wrapper used to bound a worker process (Phase 3 hardening).
///
/// 进程隔离解决"崩溃不带走宿主"，但默认仍可无限申请内存、无限派生子进程。
/// 这里在 Windows 上用 Job Object 施加三道硬约束：
///   · 进程私有内存上限（ProcessMemoryLimit）；
///   · 活动进程数 1（脚本 Worker 不应派生子进程）；
///   · KillOnJobClose：宿主释放句柄时整棵进程树一起终止。
/// CPU 硬上限通过 CpuRate 可选开启（默认关闭：不同现场核数不同，贸然 hard cap 会把慢脚本变成超时）。
/// 非 Windows 平台直接跳过，不影响现有部署。
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitProcessMemory = 0x0000_0100;
    private const uint JobObjectLimitActiveProcess = 0x0000_0008;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    private IntPtr _handle;

    private WindowsJobObject(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Assigns an already-started process to a new job. Returns null when the platform/SDK rejects it;
    /// the caller logs the error and continues (a missing hardening layer must not take the line down).
    /// </summary>
    public static WindowsJobObject? TryAssign(
        Process process,
        int memoryLimitMb,
        int cpuRatePercent,
        out string? warning)
    {
        warning = null;
        if (!OperatingSystem.IsWindows())
        {
            warning = "Windows Job Object is unavailable on this platform.";
            return null;
        }
        if (memoryLimitMb <= 0 && cpuRatePercent <= 0)
            return null;

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            warning = $"CreateJobObject failed (win32={Marshal.GetLastWin32Error()}).";
            return null;
        }

        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                        | (memoryLimitMb > 0 ? JobObjectLimitProcessMemory | JobObjectLimitActiveProcess : 0),
                    ActiveProcessLimit = memoryLimitMb > 0 ? 1u : 0u
                },
                ProcessMemoryLimit = memoryLimitMb > 0
                    ? (UIntPtr)((ulong)memoryLimitMb * 1024ul * 1024ul)
                    : UIntPtr.Zero
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)size))
                {
                    warning = $"SetInformationJobObject(memory) failed (win32={Marshal.GetLastWin32Error()}).";
                    CloseHandle(handle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                warning = $"AssignProcessToJobObject failed (win32={Marshal.GetLastWin32Error()}).";
                CloseHandle(handle);
                return null;
            }

            if (cpuRatePercent is > 0 and <= 100)
            {
                var cpu = new JobObjectCpuRateControlInformation
                {
                    ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
                    CpuRate = (uint)(cpuRatePercent * 100)
                };
                var cpuSize = Marshal.SizeOf<JobObjectCpuRateControlInformation>();
                var cpuPointer = Marshal.AllocHGlobal(cpuSize);
                try
                {
                    Marshal.StructureToPtr(cpu, cpuPointer, false);
                    if (!SetInformationJobObject(handle, JobObjectCpuRateControlInformationClass, cpuPointer, (uint)cpuSize))
                        warning = $"SetInformationJobObject(cpu) failed (win32={Marshal.GetLastWin32Error()}); memory limits remain active.";
                }
                finally
                {
                    Marshal.FreeHGlobal(cpuPointer);
                }
            }

            return new WindowsJobObject(handle);
        }
        catch (Exception ex)
        {
            warning = $"Windows Job Object setup failed: {ex.Message}";
            CloseHandle(handle);
            return null;
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }
}
