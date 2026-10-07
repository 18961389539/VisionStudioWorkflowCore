using VisionStudio.Api.Contracts;
using VisionStudio.Engine.Robot;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class RobotEndpoints
{
    public static IEndpointRouteBuilder MapRobotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/robots", (RobotManager robots) => Results.Ok(robots.List()));

        app.MapPost("/api/robots/{id}/connect", async (string id, RobotManager robots, CancellationToken ct) =>
        {
            await robots.ConnectAsync(id, ct);
            return Results.Ok(robots.Get(id));
        }).RequireOperator("robot.connect", "robot");

        app.MapPost("/api/robots/{id}/disconnect", async (string id, RobotManager robots, CancellationToken ct) =>
        {
            await robots.DisconnectAsync(id, ct);
            return Results.Ok(robots.Get(id));
        }).RequireOperator("robot.disconnect", "robot");

        app.MapPost("/api/robots/{id}/stop", async (string id, RobotManager robots, CancellationToken ct) =>
        {
            await robots.StopAsync(id, ct);
            return Results.Ok(robots.Get(id));
        }).RequireOperator("robot.stop", "robot");

        app.MapPost("/api/robots/{id}/reset", async (string id, RobotManager robots, CancellationToken ct) =>
        {
            await robots.ResetFaultAsync(id, ct);
            return Results.Ok(robots.Get(id));
        }).RequireOperator("robot.reset", "robot");

        app.MapPut("/api/robots/{id}/settings", async (string id, RobotRuntimeSettings request, RobotManager robots, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureDependencyMutationAllowed("robot", id);
            await robots.ApplySettingsAsync(id, request, ct);
            return Results.Ok(robots.Get(id));
        }).RequireEngineer("robot.settings.update", "robot");

        app.MapPost("/api/robots/{id}/target", async (
            string id,
            RobotPanelTargetRequest request,
            RobotManager robots,
            DeviceLeaseRegistry leases,
            CancellationToken ct) =>
        {
            // 请求期硬件租约：指令执行期间独占该机器人（TTL 仅作为释放路径丢失时的兜底）
            using var lease = leases.AcquireManualOrThrow("robot", id, $"Cannot command robot '{id}'");
            var target = new VisionRobotTarget2D(
                request.X, request.Y, request.RDeg, request.Frame, request.Unit, request.Robot, request.GuidanceMode);

            if (request.Action.Equals("Handshake", StringComparison.OrdinalIgnoreCase))
            {
                var result = await robots.ExecuteHandshakeAsync(id, target, new RobotCommandPolicy(
                    TimeoutMs: request.TimeoutMs,
                    MaxRetries: request.MaxRetries,
                    RetryDelayMs: request.RetryDelayMs,
                    AutoConnect: true,
                    WaitForComplete: request.WaitForInPosition,
                    AutoAck: request.AutoAck), ct);
                return Results.Ok(new { result, robot = robots.Get(id) });
            }

            RobotCommandReceipt receipt;
            if (request.Action.Equals("SendTarget", StringComparison.OrdinalIgnoreCase))
                receipt = await robots.SendTargetAsync(id, target, autoConnect: true, ct: ct);
            else
                receipt = await robots.MoveAsync(
                    id,
                    target,
                    autoConnect: true,
                    waitForInPosition: request.WaitForInPosition,
                    timeout: TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs, 50, 120000)),
                    ct: ct);

            return Results.Ok(new { receipt, robot = robots.Get(id) });
        }).RequireEngineer("robot.target.execute", "robot");

        app.MapPost("/api/robots/{id}/ack/{commandId:long}", async (string id, long commandId, RobotManager robots, CancellationToken ct) =>
        {
            await robots.AcknowledgeAsync(id, commandId, ct);
            return Results.Ok(robots.Get(id));
        }).RequireOperator("robot.command.ack", "robot");

        app.MapGet("/api/robot-traces", async (int? take, string? robotId, RobotCommandTraceStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(take ?? 100, robotId, ct)));

        app.MapGet("/api/robot-traces/{traceId}", async (string traceId, RobotCommandTraceStore store, CancellationToken ct) =>
        {
            var trace = await store.GetAsync(traceId, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        app.MapGet("/api/robot-simulators/tcp", (TcpRobotSimulatorServer simulator) => Results.Ok(new
        {
            running = simulator.Running,
            host = "127.0.0.1",
            port = simulator.Port,
            protocol = "newline-delimited JSON request/response"
        }));

        return app;
    }
}
