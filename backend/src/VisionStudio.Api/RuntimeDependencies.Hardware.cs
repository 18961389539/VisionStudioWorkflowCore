using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed partial class RuntimeDependencyManifestService
{
    /// <summary>
    /// 副作用故障恢复前的设备状态核对：锁定清单中的每个设备/机器人当前必须处于"可安全重跑"
    /// 的状态——不只是能解析、不只是非 Faulted。设备必须真正 Connected（Disconnected/Connecting/
    /// Reconnecting 都无法确认上一周期的写入是否落地）；机器人必须 Connected、非 Busy、握手无 Error
    /// 且无未完成的在途命令。核对不通过时禁止恢复——重新启动自动周期前必须完成核对并留下记录。
    ///
    /// Get 返回的是管理器状态快照，不含 PLC 动作完成标志或机器人命令确认 ID；因此对"设备动作结果
    /// 不确定"的场景采取保守拒绝：任何非 Connected、Busy 或握手异常都视为不可恢复。
    /// </summary>
    public DeviceStateVerification VerifyLockedDevices(RuntimeDependencyManifest manifest)
        => VerifyLockedDevices(
            manifest.Devices.Select(x => x.Id).ToArray(),
            manifest.Robots.Select(x => x.Id).ToArray());

    /// <summary>
    /// F02：按 ID 清单核对设备/机器人状态（与 <see cref="VerifyLockedDevices(RuntimeDependencyManifest)"/>
    /// 同一判定标准）。持久化的"故障时锁定清单"可能不属于当前发布流程——核对必须无条件针对该清单，
    /// 否则切换到无设备的新流程时，空清单会必然通过。
    /// Q02：核对条件强化——连接状态只是必要条件：
    ///   · 设备要求存在**新鲜**采样（有读数时，最新时间戳必须在阈值内），过期反馈不能证明上一动作；
    ///   · 传入指纹时校验设备身份/端点未漂移（替换成别的设备同样"Connected"）；
    ///   · 机器人额外要求无未完成的底层停止调用（Q04 登记）。
    /// </summary>
    public DeviceStateVerification VerifyLockedDevices(
        IReadOnlyList<string> deviceIds,
        IReadOnlyList<string> robotIds,
        IReadOnlyDictionary<string, string>? expectedDeviceFingerprints = null,
        TimeSpan? maxSampleAge = null)
    {
        var sampleAgeThreshold = maxSampleAge ?? MaxSampleAge;
        var issues = new List<string>();
        foreach (var deviceId in deviceIds)
        {
            try
            {
                var descriptor = devices.Get(deviceId);
                if (descriptor.ConnectionState != DeviceConnectionState.Connected)
                {
                    issues.Add($"device '{deviceId}' is {descriptor.ConnectionState}, not Connected — the previous cycle's writes cannot be confirmed ({descriptor.Error ?? "no detail"})");
                    continue;
                }
                // Q02：连接的证明力到此为止。上一动作的结果必须由**新鲜**反馈佐证：
                // 有采样值但已过期（轮询停止/通信半死）时，Connected 只说明 socket 还在。
                if (descriptor.Values.Count > 0)
                {
                    var newest = descriptor.Values.Values.Max(x => x.Timestamp);
                    var age = DateTimeOffset.UtcNow - newest;
                    if (age > sampleAgeThreshold)
                        issues.Add($"device '{deviceId}' has no fresh samples (latest is {age.TotalSeconds:0.#}s old, threshold {sampleAgeThreshold.TotalSeconds:0.#}s) — stale feedback cannot confirm the previous cycle's writes");
                }
                // 设备身份/端点漂移：替换后的设备也会报告 Connected，但已不是执行上一动作的那一个。
                if (expectedDeviceFingerprints is not null &&
                    expectedDeviceFingerprints.TryGetValue(deviceId, out var expected) &&
                    !string.IsNullOrWhiteSpace(expected))
                {
                    var actual = DeviceFingerprint(descriptor);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        issues.Add($"device '{deviceId}' identity drifted since the unconfirmed action (expected '{expected}', found '{actual}')");
                }
                if (!string.IsNullOrWhiteSpace(descriptor.Error))
                    issues.Add($"device '{deviceId}' reports error: {descriptor.Error}");
            }
            catch (Exception ex)
            {
                issues.Add($"device '{deviceId}' cannot be resolved: {ex.Message}");
            }
        }
        foreach (var robotId in robotIds)
        {
            try
            {
                var descriptor = robots.Get(robotId);
                if (descriptor.ConnectionState != RobotConnectionState.Connected)
                {
                    issues.Add($"robot '{robotId}' is {descriptor.ConnectionState}, not Connected — the previous cycle's motion cannot be confirmed ({descriptor.Error ?? "no detail"})");
                    continue;
                }
                if (descriptor.Busy)
                    issues.Add($"robot '{robotId}' is still Busy with command {descriptor.LastCommandId} — an in-flight motion was not confirmed stopped");
                if (descriptor.HandshakeState == RobotHandshakeState.Faulted || descriptor.Handshake.Error)
                    issues.Add($"robot '{robotId}' reports a handshake Error (state {descriptor.HandshakeState}, command {descriptor.LastCommandId})");
                // 未完成命令：握手停在 Executing/TargetAccepted 说明上一条命令没有被确认收尾。
                if (descriptor.HandshakeState is RobotHandshakeState.Executing or RobotHandshakeState.TargetAccepted)
                    issues.Add($"robot '{robotId}' has an unconfirmed command {descriptor.LastCommandId} in state {descriptor.HandshakeState}");
                // Q02/Q04：存在未返回的底层停止调用时，迟到的 Stop 可能落在新动作之后——不得放行。
                if (robots.HasPendingStop(robotId))
                    issues.Add($"robot '{robotId}' has an outstanding stop call that has not returned — the robot state is not yet reconciled");
                if (!string.IsNullOrWhiteSpace(descriptor.Error))
                    issues.Add($"robot '{robotId}' reports error: {descriptor.Error}");
            }
            catch (Exception ex)
            {
                issues.Add($"robot '{robotId}' cannot be resolved: {ex.Message}");
            }
        }
        return new DeviceStateVerification(issues.Count == 0, issues);
    }

    /// <summary>C05：该设备当前是否有在途命令——核对前必须确认没有已知在途动作；查询本身不拒绝/不阻断调用。</summary>
    public bool IsDeviceCommandInFlight(string deviceId)
    {
        try { return devices.IsCommandInFlight(deviceId); }
        catch { return false; } // 已注销/未注册的设备没有在途命令
    }

    /// <summary>
    /// R02（2026-10-11 复审）：核对前的相机在途检查——命令派发许可未释放（手动触发/取图触发/
    /// 同步组 Action Command 仍在设备上执行）或采集停止未确认时返回 true。无法解析的相机
    /// 保守视为未收尾。注意：普通自由连拍（Continuous 采集）不是命令在途，不阻断核对——
    /// 本检查不把"采集服务在运行"机械等同于危险动作。
    /// </summary>
    public bool IsCameraActionUnresolved(string cameraId)
    {
        try
        {
            if (commandGate is not null && commandGate.IsCommandInFlight(DeviceCommandResourceKinds.Camera, cameraId))
                return true;
            return cameras.IsStopUnconfirmed(cameraId);
        }
        catch
        {
            return true; // 无法解析：绝不当作空闲
        }
    }

    public DeviceStateVerification VerifyNoPendingRobotOperations(IReadOnlyList<string> robotIds)
    {
        var issues = new List<string>();
        foreach (var robotId in robotIds)
        {
            try
            {
                var descriptor = robots.Get(robotId);
                if (robots.HasPendingStop(robotId))
                    issues.Add($"robot '{robotId}' still has an outstanding stop call");
                if (descriptor.Busy || descriptor.HandshakeState == RobotHandshakeState.Executing)
                    issues.Add($"robot '{robotId}' still has an unconfirmed active command {descriptor.LastCommandId} ({descriptor.HandshakeState})");
            }
            catch (Exception ex) { issues.Add($"robot '{robotId}' cannot be checked for pending operations: {ex.Message}"); }
        }
        return new DeviceStateVerification(issues.Count == 0, issues);
    }

    /// <summary>Q02：设备身份指纹（driver|protocol|endpoint）——用于检测"换成另一台设备"的漂移。</summary>
    public static string DeviceFingerprint(DeviceDescriptor descriptor)
        => $"{descriptor.Driver}|{descriptor.Protocol}|{descriptor.Endpoint}";

    /// <summary>Q02：按 ID 取当前设备身份指纹（登记动作安全状态时捕获）。设备不可解析时返回 null。</summary>
    public string? TryGetDeviceFingerprint(string deviceId)
    {
        try { return DeviceFingerprint(devices.Get(deviceId)); }
        catch { return null; }
    }

    /// <summary>
    /// Q02：设备采样新鲜度阈值（可测试覆盖）。超过该阈值的反馈视为过期，不能作为动作核对证据。
    /// </summary>
    internal static TimeSpan MaxSampleAge { get; set; } = TimeSpan.FromSeconds(5);

    private async Task<IReadOnlyList<CameraRuntimeDependency>> CaptureCamerasAsync(HashSet<string> cameraIds, CancellationToken ct)
    {
        var output = new List<CameraRuntimeDependency>();
        foreach (var id in cameraIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            CameraDescriptor descriptor;
            try { descriptor = cameras.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references camera '{id}', but it is not registered.", ex); }
            var sourceProvenance = cameras.Require(id) is ICameraSourceProvenanceProvider provenanceProvider
                ? provenanceProvider.GetSourceProvenance()
                : null;
            var config = new
            {
                descriptor.Id,
                descriptor.Name,
                descriptor.Driver,
                descriptor.Source,
                sourceFingerprint = sourceProvenance?.ContentSha256,
                sourceItemCount = sourceProvenance?.ItemCount,
                sourceBytes = sourceProvenance?.TotalBytes,
                settings = descriptor.Settings,
                capabilities = descriptor.Capabilities,
                commissioningProfileHash = cameras.GetCommissioningProfileHash(descriptor.Id)
            };
            output.Add(new CameraRuntimeDependency(
                descriptor.Id,
                descriptor.Driver,
                descriptor.Source,
                HashCanonical(config),
                descriptor.Settings,
                sourceProvenance?.ContentSha256,
                await hardwareProvenance.CaptureForProductionAsync("camera", descriptor.Id, ct),
                synchronizationGroups is null ? [] : await synchronizationGroups.GetConfigurationHashesForCameraAsync(descriptor.Id, ct)));
        }
        return output;
    }

    private async Task<IReadOnlyList<DeviceRuntimeDependency>> CaptureDevicesAsync(HashSet<string> deviceIds, CancellationToken ct)
    {
        if (deviceIds.Count == 0) return [];
        var profiles = (await deviceProfiles.ListAsync(ct)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var output = new List<DeviceRuntimeDependency>();
        foreach (var id in deviceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            DeviceDescriptor descriptor;
            try { descriptor = devices.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references device '{id}', but it is not registered.", ex); }

            if (profiles.TryGetValue(id, out var profile))
            {
                var stableProfile = new
                {
                    profile.Id,
                    profile.Kind,
                    profile.Name,
                    profile.Host,
                    profile.Port,
                    profile.TimeoutMs,
                    profile.UnitId,
                    profile.RegisterOrder,
                    profile.CpuType,
                    profile.Rack,
                    profile.Slot,
                    tags = (profile.Tags ?? []).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    settings = (profile.Settings ?? new DeviceRuntimeSettings()).Normalize()
                };
                output.Add(new DeviceRuntimeDependency(
                    descriptor.Id,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    HashCanonical(stableProfile),
                    profile.UpdatedAt?.ToUniversalTime().ToString("O"),
                    await hardwareProvenance.CaptureForProductionAsync("device", descriptor.Id, ct)));
            }
            else
            {
                var stableRuntime = new
                {
                    descriptor.Id,
                    descriptor.Name,
                    descriptor.Vendor,
                    descriptor.Model,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    settings = descriptor.Settings,
                    capabilities = descriptor.Capabilities,
                    tags = descriptor.Tags.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray()
                };
                output.Add(new DeviceRuntimeDependency(
                    descriptor.Id,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    HashCanonical(stableRuntime),
                    null,
                    await hardwareProvenance.CaptureForProductionAsync("device", descriptor.Id, ct)));
            }
        }
        return output;
    }

    private async Task<IReadOnlyList<RobotRuntimeDependency>> CaptureRobotsAsync(HashSet<string> robotIds, CancellationToken ct)
    {
        var output = new List<RobotRuntimeDependency>();
        foreach (var id in robotIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            RobotDescriptor descriptor;
            try { descriptor = robots.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references robot '{id}', but it is not registered.", ex); }
            var stable = new
            {
                descriptor.Id,
                descriptor.Name,
                descriptor.Vendor,
                descriptor.Model,
                descriptor.Driver,
                descriptor.BaseFrame,
                descriptor.Unit,
                settings = descriptor.Settings,
                capabilities = descriptor.Capabilities
            };
            output.Add(new RobotRuntimeDependency(
                descriptor.Id,
                descriptor.Driver,
                descriptor.Vendor,
                descriptor.Model,
                descriptor.BaseFrame,
                descriptor.Unit,
                HashCanonical(stable),
                await hardwareProvenance.CaptureForProductionAsync("robot", descriptor.Id, ct)));
        }
        return output;
    }
}
