using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 传动/编码器标定（calibration.conveyor，2026-10-27 · 缺口分析 P1-11）。
///
/// 现场缺口：`calibration.planar` 只解决"像素↔毫米"，`handEye2D/3D` 解决"相机↔机器人"，
/// 但传送带动态抓取还差最关键的一环——**编码器计数 ↔ 工件实际位置**，以及
/// **触发延迟期间工件又走了多远**。这一环之前只能在上位机里写死，换线体就得改代码。
///
/// 本节点只做这两步换算，不碰硬件：
/// - `position = counts / countsPerUnit`：编码器计数折算成工程单位；
/// - `triggerOffset = lineSpeed × triggerDelayMs / 1000`：从"触发拍照"到"实际抓取/判定"之间
///   工件继续前进的距离；
/// - `compensatedPosition = position ± triggerOffset`：正/反转线体用 compensationSign 切换。
///
/// 为什么单独成节点而不是塞进相机驱动：编码器比例和线速是**工艺参数**，换产品就要改；
/// 放在流程里能被配方管理、能被追溯记录，也不会因为换相机品牌而重写。
/// </summary>
public sealed class ConveyorCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.conveyor";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var counts = context.TryGetValue("encoder", out var encoderInput)
            ? encoderInput.Require<double>("encoder")
            : node.GetDouble("encoder", 0);
        if (!double.IsFinite(counts) || Math.Abs(counts) > 1e15)
            throw new InvalidOperationException("calibration.conveyor encoder count must be finite.");

        var maxEncoderAgeMs = node.GetDouble("maxEncoderAgeMs", 0);
        if (!double.IsFinite(maxEncoderAgeMs) || maxEncoderAgeMs is < 0 or > 60000)
            throw new InvalidOperationException("calibration.conveyor maxEncoderAgeMs must be finite within 0..60000 (0 disables).");
        var encoderSampleId = context.TryGetValue("encoderSampleId", out var sampleInput)
            ? sampleInput.Require<long>("encoderSampleId")
            : node.GetInt("encoderSampleId", 0);
        var previousEncoderSampleId = context.TryGetValue("previousEncoderSampleId", out var previousInput)
            ? previousInput.Require<long>("previousEncoderSampleId")
            : node.GetInt("previousEncoderSampleId", 0);
        if (encoderSampleId < 0 || previousEncoderSampleId < 0)
            throw new InvalidOperationException("calibration.conveyor encoder sample ids must be >= 0.");
        if (encoderSampleId > 0 && previousEncoderSampleId > 0 && encoderSampleId <= previousEncoderSampleId)
            throw new InvalidOperationException($"calibration.conveyor encoderSampleId {encoderSampleId} is not newer than previousEncoderSampleId {previousEncoderSampleId}; re-latch the encoder.");
        var latchedAtText = context.TryGetValue("encoderLatchedAt", out var latchedInput)
            ? latchedInput.Require<string>("encoderLatchedAt")
            : node.GetString("encoderLatchedAt", "");
        var triggeredAtText = context.TryGetValue("triggeredAt", out var triggeredInput)
            ? triggeredInput.Require<string>("triggeredAt")
            : node.GetString("triggeredAt", "");
        DateTimeOffset? latchedAt = null;
        DateTimeOffset? triggeredAt = null;
        if (!string.IsNullOrWhiteSpace(latchedAtText))
        {
            if (!DateTimeOffset.TryParse(latchedAtText, out var parsed)) throw new InvalidOperationException("calibration.conveyor encoderLatchedAt must be an ISO-8601 timestamp.");
            latchedAt = parsed;
        }
        if (!string.IsNullOrWhiteSpace(triggeredAtText))
        {
            if (!DateTimeOffset.TryParse(triggeredAtText, out var parsed)) throw new InvalidOperationException("calibration.conveyor triggeredAt must be an ISO-8601 timestamp.");
            triggeredAt = parsed;
        }
        double? encoderAgeMs = latchedAt is { } latch && triggeredAt is { } trigger ? (trigger - latch).TotalMilliseconds : null;
        if (maxEncoderAgeMs > 0)
        {
            if (encoderAgeMs is not { } latchAge)
                throw new InvalidOperationException("calibration.conveyor maxEncoderAgeMs requires both encoderLatchedAt and triggeredAt.");
            if (latchAge < 0 || latchAge > maxEncoderAgeMs)
                throw new InvalidOperationException($"calibration.conveyor encoder latch age {latchAge:0.###} ms is outside 0..maxEncoderAgeMs={maxEncoderAgeMs:0.###}; re-latch the encoder.");
        }

        var countsPerUnit = node.GetDouble("countsPerUnit", 1000);
        if (!double.IsFinite(countsPerUnit) || countsPerUnit <= 0)
            throw new InvalidOperationException(
                "calibration.conveyor countsPerUnit must be > 0 (encoder counts per millimetre of travel).");

        var unit = node.GetString("unit", "mm").Trim();
        if (unit.Length is < 1 or > 16)
            throw new InvalidOperationException("calibration.conveyor unit must be 1..16 characters.");

        var triggerDelayMs = node.GetDouble("triggerDelayMs", 0);
        if (!double.IsFinite(triggerDelayMs) || triggerDelayMs is < 0 or > 60000)
            throw new InvalidOperationException("calibration.conveyor triggerDelayMs must be finite within 0..60000.");

        var lineSpeed = node.GetDouble("lineSpeedUnitPerSec", 0);
        if (!double.IsFinite(lineSpeed) || lineSpeed is < 0 or > 1e6)
            throw new InvalidOperationException("calibration.conveyor lineSpeedUnitPerSec must be finite within 0..1000000.");

        var sign = node.GetString("compensationSign", "add").Trim().ToLowerInvariant();
        if (sign is not ("add" or "subtract"))
            throw new InvalidOperationException("calibration.conveyor compensationSign must be add or subtract.");

        var configuredDirection = Choice(node, "lineDirection", "unconfirmed", "unconfirmed", "forward", "reverse");
        var observedDirection = context.TryGetValue("speedDirection", out var directionInput)
            ? directionInput.Require<string>("speedDirection").Trim().ToLowerInvariant()
            : node.GetString("speedDirection", "unknown").Trim().ToLowerInvariant();
        if (observedDirection is not ("unknown" or "forward" or "reverse"))
            throw new InvalidOperationException("calibration.conveyor speedDirection must be unknown, forward or reverse.");
        var motionState = context.TryGetValue("motionState", out var stateInput)
            ? stateInput.Require<string>("motionState").Trim().ToLowerInvariant()
            : node.GetString("motionState", "unknown").Trim().ToLowerInvariant();
        if (motionState is not ("unknown" or "steady" or "decelerating" or "reversing" or "stopped"))
            throw new InvalidOperationException("calibration.conveyor motionState must be unknown, steady, decelerating, reversing or stopped.");

        var position = counts / countsPerUnit;
        var triggerOffset = lineSpeed * triggerDelayMs / 1000.0;
        var expectedSign = configuredDirection == "forward" ? "add" : "subtract";
        var directionConfirmed = configuredDirection != "unconfirmed" && observedDirection == configuredDirection;
        var motionAllowed = motionState == "steady" || motionState == "stopped" && lineSpeed == 0;
        var signAllowed = configuredDirection != "unconfirmed" && sign == expectedSign;
        var targetAllowed = directionConfirmed && motionAllowed && signAllowed;
        var reason = targetAllowed ? "ok"
            : configuredDirection == "unconfirmed" ? "line direction is not confirmed"
            : observedDirection != configuredDirection ? "observed speed direction conflicts with configured line direction"
            : !motionAllowed ? "robot target blocked while conveyor is decelerating, reversing or motion state is unknown"
            : "compensationSign conflicts with configured line direction";
        var compensated = sign == "add" ? position + triggerOffset : position - triggerOffset;

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("unit", unit), ("counts", counts), ("countsPerUnit", countsPerUnit),
            ("encoderSampleId", encoderSampleId), ("previousEncoderSampleId", previousEncoderSampleId),
            ("encoderLatchedAt", latchedAt?.ToString("O")), ("triggeredAt", triggeredAt?.ToString("O")),
            ("encoderAgeMs", encoderAgeMs), ("maxEncoderAgeMs", maxEncoderAgeMs),
            ("position", position), ("triggerOffset", triggerOffset),
            ("lineSpeed", lineSpeed), ("triggerDelayMs", triggerDelayMs), ("sign", sign),
            ("lineDirection", configuredDirection), ("speedDirection", observedDirection),
            ("motionState", motionState), ("targetAllowed", targetAllowed), ("reason", reason));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["position"] = VisionValue.Double(position),
            ["triggerOffset"] = VisionValue.Double(triggerOffset),
            ["targetAllowed"] = VisionValue.Boolean(targetAllowed),
            ["unit"] = VisionValue.String(unit),
            ["encoderAgeMs"] = encoderAgeMs is { } age ? VisionValue.Double(age) : new VisionValue(VisionDataType.Double, null),
            ["encoderSampleId"] = VisionValue.Integer(encoderSampleId),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        var summary = new Dictionary<string, object?>
        {
            ["unit"] = unit, ["counts"] = counts, ["countsPerUnit"] = countsPerUnit,
            ["encoderSampleId"] = encoderSampleId, ["previousEncoderSampleId"] = previousEncoderSampleId,
            ["encoderLatchedAt"] = latchedAt?.ToString("O"), ["triggeredAt"] = triggeredAt?.ToString("O"),
            ["encoderAgeMs"] = encoderAgeMs, ["maxEncoderAgeMs"] = maxEncoderAgeMs,
            ["position"] = position, ["triggerOffset"] = triggerOffset,
            ["lineSpeed"] = lineSpeed, ["triggerDelayMs"] = triggerDelayMs, ["sign"] = sign,
            ["lineDirection"] = configuredDirection, ["speedDirection"] = observedDirection,
            ["motionState"] = motionState, ["targetAllowed"] = targetAllowed, ["reason"] = reason,
            ["diagnostics"] = diagnostics
        };
        if (targetAllowed)
        {
            outputs["compensatedPosition"] = VisionValue.Double(compensated);
            summary["compensatedPosition"] = compensated;
        }

        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    private static string Choice(NodeDefinition node, string name, string fallback, params string[] choices)
    {
        var value = node.GetString(name, fallback).Trim().ToLowerInvariant();
        return choices.FirstOrDefault(choice => choice == value)
            ?? throw new InvalidOperationException($"calibration.conveyor {name} must be {string.Join(", ", choices)}.");
    }
}
