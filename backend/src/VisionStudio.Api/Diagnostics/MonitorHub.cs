using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Diagnostics;

/// <summary>
/// 设备监视推送通道（2026-11-12）。
///
/// 之前每个面板各自 <c>setInterval</c> 拉 HTTP：RobotPanel 300 ms、CameraPanel 1 s + 预览 500 ms、
/// DevicePanel 1 s、生产监控 1 s。单客户端十几个请求/秒，而这些请求的返回**绝大多数没变化**——
/// 真正的状态变化是低频事件（连上、掉线、故障），用轮询去"发现"它，成本与延迟都不划算。
///
/// 这里的语义刻意保守：
/// <list type="bullet">
/// <item>服务端每秒采集一次**轻量列表**（相机/设备/机器人描述符），**内容变了才广播**；</item>
/// <item>没有客户端连接时不做序列化、不保留基线（连上后第一次 tick 必定推一份完整快照）；</item>
/// <item>前端收到通知后再拉自己需要的详情接口，轮询降为 10 s 兜底——推送负责"快"，
///       轮询负责"断线后不会永远停在旧状态"。</item>
/// </list>
/// 不推送单个设备的细粒度遥测（帧率、轴坐标）：那些由各自面板按需拉取，塞进每秒广播会让
/// 消息体膨胀到与轮询同样贵。
/// </summary>
public sealed class MonitorHub : Hub
{
    private readonly MonitorSubscriptions _subscriptions;

    public MonitorHub(MonitorSubscriptions subscriptions) => _subscriptions = subscriptions;

    public override Task OnConnectedAsync()
    {
        _subscriptions.Join();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _subscriptions.Leave();
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>在线订阅数。单独成类而不是 Hub 上的静态字段：静态状态在测试之间会残留。</summary>
public sealed class MonitorSubscriptions
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public bool HasClients => Count > 0;

    public void Join() => Interlocked.Increment(ref _count);

    public void Leave() => Interlocked.Decrement(ref _count);
}

/// <summary>
/// 一次监视快照。字段就是三个列表接口的返回体——前端拿到它可以直接替换本地状态，
/// 不必为"推送格式"再维护一套映射。
/// </summary>
public sealed record MonitorSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CameraDescriptor> Cameras,
    IReadOnlyList<DeviceDescriptor> Devices,
    IReadOnlyList<RobotDescriptor> Robots);

/// <summary>
/// 每秒采集一次设备描述符，内容变化时广播 <c>monitorSnapshot</c>。
/// 采集的是 <c>List()</c>（纯内存读），不发命令、不碰硬件。
/// </summary>
public sealed class MonitorBroadcastService(
    IHubContext<MonitorHub> hub,
    MonitorSubscriptions subscriptions,
    CameraManager cameras,
    DeviceManager devices,
    RobotManager robots,
    ILogger<MonitorBroadcastService> logger) : BackgroundService
{
    /// <summary>采集周期。1 s 与人的操作节拍一致；再快只是把"变化"重复广播。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        string? lastPayload = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
                if (!subscriptions.HasClients)
                {
                    // 无订阅者：清基线，保证下一个客户端连上后立刻收到一份完整快照，
                    // 而不是"内容没变所以不发"，让新面板空等到兜底轮询。
                    lastPayload = null;
                    continue;
                }

                var snapshot = new MonitorSnapshot(
                    DateTimeOffset.UtcNow,
                    cameras.List(),
                    devices.List(),
                    robots.List());
                var payload = JsonSerializer.Serialize(snapshot, Json);
                if (string.Equals(payload, lastPayload, StringComparison.Ordinal)) continue;
                lastPayload = payload;
                await hub.Clients.All.SendAsync("monitorSnapshot", snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 监视通道是辅助设施：它挂掉不能让宿主退出，也不能让循环停在这里。
                // 记录后退避一轮，下一 tick 继续尝试（硬件管理器抛异常通常与设备状态有关）。
                logger.LogWarning(ex, "Monitor broadcast tick failed; will retry next interval.");
                lastPayload = null;
                await Task.Delay(Interval, stoppingToken);
            }
        }
    }
}
