using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class RobotCurrentPoseNode(RobotManager robots) : IVisionNodeExecutor
{
    public string Type => "robot.currentPose";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var robotId = node.GetString("robotId", "virtual-abb-1");
        var autoConnect = node.GetBool("autoConnect", true);
        var snapshot = robots.Get(robotId);
        if (snapshot.ConnectionState != RobotConnectionState.Connected)
        {
            if (!autoConnect)
                throw new InvalidOperationException($"Robot '{robotId}' is disconnected and Auto Connect is disabled.");
            await robots.ConnectAsync(robotId, cancellationToken);
            snapshot = robots.Get(robotId);
        }

        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pose"] = VisionValue.CoordinatePose(snapshot.CurrentPose),
                ["busy"] = VisionValue.Boolean(snapshot.Busy),
                ["inPosition"] = VisionValue.Boolean(snapshot.InPosition),
                ["targetReady"] = VisionValue.Boolean(snapshot.Handshake.TargetReady),
                ["execute"] = VisionValue.Boolean(snapshot.Handshake.Execute),
                ["complete"] = VisionValue.Boolean(snapshot.Handshake.Complete),
                ["handshakeError"] = VisionValue.Boolean(snapshot.Handshake.Error),
                ["ack"] = VisionValue.Boolean(snapshot.Handshake.Ack),
                ["state"] = VisionValue.String(snapshot.HandshakeState.ToString()),
                ["connection"] = VisionValue.String(snapshot.ConnectionState.ToString()),
                ["error"] = VisionValue.String(snapshot.Error ?? snapshot.Handshake.ErrorCode ?? string.Empty)
            },
            new Dictionary<string, object?>
            {
                ["robotId"] = snapshot.Id,
                ["pose"] = $"X={snapshot.CurrentPose.X:0.###}, Y={snapshot.CurrentPose.Y:0.###}, R={snapshot.CurrentPose.ThetaDeg:0.###}°",
                ["frame"] = snapshot.CurrentPose.Frame,
                ["state"] = snapshot.HandshakeState.ToString(),
                ["connection"] = snapshot.ConnectionState.ToString(),
                ["busy"] = snapshot.Busy,
                ["inPosition"] = snapshot.InPosition,
                ["handshake"] = snapshot.Handshake
            });
    }
}

public sealed class RobotExecuteTargetNode(RobotManager robots) : IVisionNodeExecutor
{
    public string Type => "robot.executeTarget";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var target = context.Require<VisionRobotTarget2D>("target");
        var robotId = node.GetString("robotId", "virtual-abb-1");
        var action = node.GetString("action", "Handshake");
        var autoConnect = node.GetBool("autoConnect", true);
        var wait = node.GetBool("waitForInPosition", true);
        var timeoutMs = Math.Clamp(node.GetDouble("timeoutMs", 5000), 50, 120000);

        RobotCommandReceipt receipt;
        string traceId = string.Empty;
        var attempts = 1;
        if (action.Equals("SendTarget", StringComparison.OrdinalIgnoreCase))
        {
            receipt = await robots.SendTargetAsync(robotId, target, autoConnect, cancellationToken);
        }
        else if (action.Equals("Move", StringComparison.OrdinalIgnoreCase))
        {
            receipt = await robots.MoveAsync(robotId, target, autoConnect, wait, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        }
        else if (action.Equals("Handshake", StringComparison.OrdinalIgnoreCase))
        {
            var policy = new RobotCommandPolicy(
                TimeoutMs: (int)timeoutMs,
                MaxRetries: Math.Clamp((int)node.GetDouble("maxRetries", 1), 0, 10),
                RetryDelayMs: Math.Clamp((int)node.GetDouble("retryDelayMs", 100), 0, 10000),
                AutoConnect: autoConnect,
                WaitForComplete: wait,
                AutoAck: node.GetBool("autoAck", true));
            var result = await robots.ExecuteHandshakeAsync(robotId, target, policy, cancellationToken);
            receipt = result.Receipt;
            traceId = result.TraceId;
            attempts = result.Attempts;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported robot action '{action}'. Use SendTarget, Move or Handshake.");
        }

        var snapshot = robots.Get(robotId);
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["target"] = VisionValue.RobotTarget(target),
                ["currentPose"] = VisionValue.CoordinatePose(snapshot.CurrentPose),
                ["commandId"] = VisionValue.Integer(receipt.CommandId),
                ["inPosition"] = VisionValue.Boolean(snapshot.InPosition),
                ["state"] = VisionValue.String(snapshot.HandshakeState.ToString()),
                ["traceId"] = VisionValue.String(traceId),
                ["attempts"] = VisionValue.Integer(attempts)
            },
            new Dictionary<string, object?>
            {
                ["robotId"] = robotId,
                ["action"] = action,
                ["commandId"] = receipt.CommandId,
                ["traceId"] = traceId,
                ["attempts"] = attempts,
                ["state"] = snapshot.HandshakeState.ToString(),
                ["handshake"] = snapshot.Handshake,
                ["inPosition"] = snapshot.InPosition,
                ["currentPose"] = $"X={snapshot.CurrentPose.X:0.###}, Y={snapshot.CurrentPose.Y:0.###}, R={snapshot.CurrentPose.ThetaDeg:0.###}°",
                ["target"] = $"X={target.X:0.###}, Y={target.Y:0.###}, R={target.RDeg:0.###}°"
            });
    }
}
