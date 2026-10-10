using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api;

/// <summary>
/// R02：基于设备层真实状态的租约接管探针。
///
/// 租约 TTL 到期只说明持有者可能失联（例如请求线程崩溃、厂商调用卡死），**不能**作为
/// "设备可用"的证据：机器人等待上限 120 秒、驱动的 StopAsync 可能忽略取消，都可能让原操作
/// 在 TTL 之后仍然活跃。因此接管的判定必须回到设备层：
///   · 设备：命令门被占用（有在途 PLC 读写）⇒ 忙碌；
///   · 机器人：命令门被占用、仍 Busy、或存在未返回的停止调用 ⇒ 忙碌；
///   · 相机：处于采集/收尾/隔离状态（Stopped/Faulted 之外）⇒ 忙碌；
///   · 资源无法解析（已注销/未注册）⇒ 保守视为忙碌，拒绝接管。
/// </summary>
public sealed class DeviceActivityProbe(
    DeviceManager devices,
    RobotManager robots,
    CameraManager cameras) : IDeviceActivityProbe
{
    public bool IsResourceBusy(string resourceKind, string resourceId)
    {
        try
        {
            return resourceKind switch
            {
                DeviceLeaseResourceKinds.Device => devices.IsCommandInFlight(resourceId),
                DeviceLeaseResourceKinds.Robot =>
                    robots.IsCommandInFlight(resourceId) ||
                    robots.Get(resourceId).Busy ||
                    robots.HasPendingStop(resourceId),
                DeviceLeaseResourceKinds.Camera => cameras.IsAcquisitionActive(resourceId),
                _ => true
            };
        }
        catch
        {
            // 资源不可解析：绝不当作空闲——拒绝接管是本层的安全默认。
            return true;
        }
    }
}
