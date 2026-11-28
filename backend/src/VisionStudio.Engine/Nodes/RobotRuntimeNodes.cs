using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Nodes;

public sealed class RobotCurrentPoseNode(RobotManager robots) : IVisionNodeExecutor
{
    public string Type => "robot.currentPose";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
if (context is null) throw new ArgumentNullException(nameof(context));

        var robotId = node.GetString("robotId", "virtual-abb-1").Trim();
        if (string.IsNullOrWhiteSpace(robotId)) throw new InvalidOperationException("robot.currentPose robotId must be non-empty.");
        if (robotId.Length > 128) throw new InvalidOperationException("robot.currentPose robotId must be at most 128 characters.");
        var autoConnect = node.GetBool("autoConnect", true);
        var requireInPosition = node.GetBool("requireInPosition", false);
        var requireNoError = node.GetBool("requireNoError", false);
        var maxDataAgeMs = node.GetDouble("maxDataAgeMs", 0);
        if (!double.IsFinite(maxDataAgeMs) || maxDataAgeMs < 0)
            throw new InvalidOperationException("robot.currentPose maxDataAgeMs must be finite and >= 0 (0 disables).");
        var expectedFrame = node.GetString("expectedFrame", "").Trim();
        RobotDescriptor snapshot;
        var autoConnected = false;
        var readStage = "snapshot";
        try
        {
            snapshot = robots.Get(robotId);
            if (snapshot.ConnectionState != RobotConnectionState.Connected)
            {
                if (!autoConnect)
                    throw new InvalidOperationException($"Robot '{robotId}' is disconnected and Auto Connect is disabled.");
                readStage = "connect";
                await robots.ConnectAsync(robotId, cancellationToken);
                autoConnected = true;
                readStage = "snapshot";
                snapshot = robots.Get(robotId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException($"robot.currentPose failed for '{robotId}': {ex.Message}", ex);
        }

        var pose = snapshot.CurrentPose;
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.ThetaDeg))
            throw new InvalidOperationException($"robot.currentPose returned non-finite pose from '{robotId}'.");
        if (expectedFrame.Length > 0 && !string.Equals(pose.Frame, expectedFrame, StringComparison.Ordinal))
            throw new InvalidOperationException($"robot.currentPose expectedFrame '{expectedFrame}' does not match pose frame '{pose.Frame}'.");
        var errorText = snapshot.Error ?? snapshot.Handshake.ErrorCode ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var dataAgeMs = snapshot.UpdatedAt == default ? -1d : Math.Max(0d, (now - snapshot.UpdatedAt).TotalMilliseconds);
        var dataFresh = snapshot.UpdatedAt != default && snapshot.UpdatedAt <= now.AddSeconds(1)
            && (maxDataAgeMs <= 0 || dataAgeMs <= maxDataAgeMs);
        if (!dataFresh)
            throw new InvalidOperationException($"robot.currentPose data is stale or has no valid update timestamp for '{robotId}' (ageMs={dataAgeMs:0}, maxDataAgeMs={maxDataAgeMs:0}).");
        var connectionReason = snapshot.ConnectionState == RobotConnectionState.Connected
            ? errorText.Length == 0 ? "connected" : errorText
            : snapshot.Error ?? snapshot.Handshake.ErrorCode ?? snapshot.ConnectionState.ToString();
        if (requireInPosition && !snapshot.InPosition)
            throw new InvalidOperationException($"robot.currentPose requireInPosition rejected robot '{robotId}' (state={snapshot.HandshakeState}).");
        if (requireNoError && (errorText.Length > 0 || snapshot.Handshake.Error))
            throw new InvalidOperationException($"robot.currentPose requireNoError rejected robot '{robotId}': {errorText}");
        var diag = CoordinateValidation.Diagnostics(Type,
            ("robotId", snapshot.Id), ("frame", pose.Frame), ("unit", pose.Unit),
            ("x", pose.X), ("y", pose.Y), ("thetaDeg", pose.ThetaDeg),
            ("busy", snapshot.Busy), ("inPosition", snapshot.InPosition),
            ("state", snapshot.HandshakeState.ToString()), ("connection", snapshot.ConnectionState.ToString()),
            ("autoConnect", autoConnect), ("autoConnected", autoConnected), ("readStage", readStage),
            ("lastUpdated", snapshot.UpdatedAt.ToString("O")), ("dataAgeMs", dataAgeMs), ("dataFresh", dataFresh),
            ("maxDataAgeMs", maxDataAgeMs), ("connectionReason", connectionReason),
            ("requireInPosition", requireInPosition), ("requireNoError", requireNoError), ("expectedFrame", expectedFrame), ("hasError", errorText.Length > 0));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pose"] = VisionValue.CoordinatePose(pose),
                ["busy"] = VisionValue.Boolean(snapshot.Busy),
                ["inPosition"] = VisionValue.Boolean(snapshot.InPosition),
                ["targetReady"] = VisionValue.Boolean(snapshot.Handshake.TargetReady),
                ["execute"] = VisionValue.Boolean(snapshot.Handshake.Execute),
                ["complete"] = VisionValue.Boolean(snapshot.Handshake.Complete),
                ["handshakeError"] = VisionValue.Boolean(snapshot.Handshake.Error),
                ["ack"] = VisionValue.Boolean(snapshot.Handshake.Ack),
                ["state"] = VisionValue.String(snapshot.HandshakeState.ToString()),
                ["connection"] = VisionValue.String(snapshot.ConnectionState.ToString()),
                ["error"] = VisionValue.String(errorText),
                ["x"] = VisionValue.Double(pose.X),
                ["y"] = VisionValue.Double(pose.Y),
                ["thetaDeg"] = VisionValue.Double(pose.ThetaDeg),
                ["hasError"] = VisionValue.Boolean(errorText.Length > 0 || snapshot.Handshake.Error),
                ["lastUpdated"] = VisionValue.String(snapshot.UpdatedAt.ToString("O")),
                ["dataAgeMs"] = VisionValue.Double(dataAgeMs),
                ["dataFresh"] = VisionValue.Boolean(dataFresh),
                ["autoConnected"] = VisionValue.Boolean(autoConnected),
                ["connectionReason"] = VisionValue.String(connectionReason),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["robotId"] = snapshot.Id,
                ["pose"] = $"X={pose.X:0.###}, Y={pose.Y:0.###}, R={pose.ThetaDeg:0.###}",
                ["frame"] = pose.Frame,
                ["state"] = snapshot.HandshakeState.ToString(),
                ["connection"] = snapshot.ConnectionState.ToString(),
                ["busy"] = snapshot.Busy,
                ["inPosition"] = snapshot.InPosition,
                ["handshake"] = snapshot.Handshake,
                ["lastUpdated"] = snapshot.UpdatedAt,
                ["dataAgeMs"] = dataAgeMs,
                ["dataFresh"] = dataFresh,
                ["connectionReason"] = connectionReason,
                ["diagnostics"] = diag
            });
    }
}

public sealed class RobotExecuteTargetNode(RobotManager robots) : IVisionNodeExecutor
{
    public string Type => "robot.executeTarget";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
if (context is null) throw new ArgumentNullException(nameof(context));

        var target = context.Require<VisionRobotTarget2D>("target");
        var robotId = node.GetString("robotId", "virtual-abb-1").Trim();
        if (string.IsNullOrWhiteSpace(robotId)) throw new InvalidOperationException("robot.executeTarget robotId must be non-empty.");
        var action = node.GetString("action", "Handshake");
        var autoConnect = node.GetBool("autoConnect", true);
        var wait = node.GetBool("waitForInPosition", true);
        var timeoutMs = node.GetDouble("timeoutMs", 5000);
        if (!double.IsFinite(timeoutMs) || timeoutMs is < 50 or > 120000) throw new InvalidOperationException("robot.executeTarget timeoutMs must be between 50 and 120000.");

        RobotCommandReceipt receipt;
        RobotDescriptor snapshot;
        string traceId = string.Empty;
        var attempts = 1;
        try
        {
            if (action.Equals("SendTarget", StringComparison.OrdinalIgnoreCase))
            {
                receipt = await robots.SendTargetAsync(robotId, target, autoConnect, cancellationToken);
                snapshot = robots.Get(robotId);
            }
            else if (action.Equals("Move", StringComparison.OrdinalIgnoreCase))
            {
                receipt = await robots.MoveAsync(robotId, target, autoConnect, wait, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
                snapshot = robots.Get(robotId);
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
                snapshot = result.Robot;
                traceId = result.TraceId;
                attempts = result.Attempts;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported robot action '{action}'. Use SendTarget, Move or Handshake.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failureKind = ClassifyRobotFailure(robots, robotId, ex);
            ex.Data["failureKind"] = failureKind;
            ex.Data["robotId"] = robotId;
            ex.Data["timeoutMs"] = timeoutMs;
            ex.Data["waitForInPosition"] = wait;
            if (robots.TryGet(robotId, out var failedSnapshot))
            {
                ex.Data["connection"] = failedSnapshot.ConnectionState.ToString();
                ex.Data["state"] = failedSnapshot.HandshakeState.ToString();
                ex.Data["inPosition"] = failedSnapshot.InPosition;
                ex.Data["positionTolerance"] = failedSnapshot.Settings.PositionToleranceMm;
                ex.Data["angleTolerance"] = failedSnapshot.Settings.AngleToleranceDeg;
            }
            throw;
        }

        var diag = CoordinateValidation.Diagnostics(Type,
            ("robotId", robotId), ("action", action), ("commandId", receipt.CommandId),
            ("attempts", attempts), ("inPosition", snapshot.InPosition), ("state", snapshot.HandshakeState.ToString()),
            ("timeoutMs", timeoutMs), ("waitForInPosition", wait), ("autoConnect", autoConnect),
            ("positionTolerance", snapshot.Settings.PositionToleranceMm), ("angleTolerance", snapshot.Settings.AngleToleranceDeg),
            ("failureKind", "none"));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["target"] = VisionValue.RobotTarget(target),
                ["currentPose"] = VisionValue.CoordinatePose(snapshot.CurrentPose),
                ["commandId"] = VisionValue.Integer(receipt.CommandId),
                ["inPosition"] = VisionValue.Boolean(snapshot.InPosition),
                ["state"] = VisionValue.String(snapshot.HandshakeState.ToString()),
                ["traceId"] = VisionValue.String(traceId),
                ["attempts"] = VisionValue.Integer(attempts),
                ["failureKind"] = VisionValue.String("none"),
                ["positionTolerance"] = VisionValue.Double(snapshot.Settings.PositionToleranceMm),
                ["angleTolerance"] = VisionValue.Double(snapshot.Settings.AngleToleranceDeg),
                ["diagnostics"] = VisionValue.String(diag)
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
                ["failureKind"] = "none",
                ["positionTolerance"] = snapshot.Settings.PositionToleranceMm,
                ["angleTolerance"] = snapshot.Settings.AngleToleranceDeg,
                ["currentPose"] = $"X={snapshot.CurrentPose.X:0.###}, Y={snapshot.CurrentPose.Y:0.###}, R={snapshot.CurrentPose.ThetaDeg:0.###}",
                ["target"] = $"X={target.X:0.###}, Y={target.Y:0.###}, R={target.RDeg:0.###}",
                ["diagnostics"] = diag
            });
    }

    private static string ClassifyRobotFailure(RobotManager robots, string robotId, Exception error)
    {
        if (robots.TryGet(robotId, out var snapshot))
        {
            if (snapshot.ConnectionState != RobotConnectionState.Connected)
                return snapshot.ConnectionState == RobotConnectionState.Faulted ? "connection-fault" : "connection-lost";
            if (snapshot.Handshake.Error || snapshot.HandshakeState == RobotHandshakeState.Faulted)
                return "robot-error";
        }
        if (error is RobotCommandTimeoutException)
            return error.Message.Contains("InPosition", StringComparison.OrdinalIgnoreCase)
                ? "in-position-timeout" : "completion-timeout";
        if (error is TimeoutException) return "timeout";
        if (error.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)) return "connection";
        return "dispatch";
    }
}
