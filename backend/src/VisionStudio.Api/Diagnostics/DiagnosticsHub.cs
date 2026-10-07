using Microsoft.AspNetCore.SignalR;

namespace VisionStudio.Api.Diagnostics;

public sealed class DiagnosticsHub : Hub
{
    public Task Ping() => Clients.Caller.SendAsync("pong", DateTimeOffset.UtcNow);
}
