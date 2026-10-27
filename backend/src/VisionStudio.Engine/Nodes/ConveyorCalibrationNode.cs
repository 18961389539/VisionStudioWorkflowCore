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

        var position = counts / countsPerUnit;
        var triggerOffset = lineSpeed * triggerDelayMs / 1000.0;
        var compensated = sign == "add" ? position + triggerOffset : position - triggerOffset;

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("unit", unit), ("counts", counts), ("countsPerUnit", countsPerUnit),
            ("position", position), ("triggerOffset", triggerOffset), ("compensatedPosition", compensated),
            ("lineSpeed", lineSpeed), ("triggerDelayMs", triggerDelayMs), ("sign", sign));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["position"] = VisionValue.Double(position),
                ["triggerOffset"] = VisionValue.Double(triggerOffset),
                ["compensatedPosition"] = VisionValue.Double(compensated),
                ["unit"] = VisionValue.String(unit),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["unit"] = unit, ["counts"] = counts, ["countsPerUnit"] = countsPerUnit,
                ["position"] = position, ["triggerOffset"] = triggerOffset,
                ["compensatedPosition"] = compensated, ["lineSpeed"] = lineSpeed,
                ["triggerDelayMs"] = triggerDelayMs, ["sign"] = sign,
                ["diagnostics"] = diagnostics
            }));
    }
}
