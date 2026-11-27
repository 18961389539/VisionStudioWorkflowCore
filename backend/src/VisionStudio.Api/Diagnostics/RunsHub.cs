using Microsoft.AspNetCore.SignalR;

namespace VisionStudio.Api.Diagnostics;

/// <summary>
/// 实时运行进度与取消状态推送 Hub（/hubs/runs）。
/// </summary>
public sealed class RunsHub : Hub
{
    public Task Ping() => Clients.Caller.SendAsync("pong", DateTimeOffset.UtcNow);
}
