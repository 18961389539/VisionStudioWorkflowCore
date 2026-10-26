using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 结果落库（output.database，2026-10-26 · 缺口分析 P1-12）。
///
/// 现状：结果只能写文件（output.exportResult）或 HTTP 推走；要入库必须在上位机再写一套接收端。
///
/// 分层取舍：节点不认识任何数据库。它把结果序列化成一条 <see cref="ResultRecord"/>，交给宿主注册的
/// <see cref="IResultRecordStore"/>。好处是换存储不用改配方，引擎也不用背数据库驱动依赖。
///
/// 失败语义：**没有注册存储时直接失败**，不静默丢弃。静默丢弃会让"已入库"的假设在事后对账时才暴露，
/// 那是产线最难查的一类问题。
///
/// 负载形态与 output.exportResult 的 JSON 保持一致（<c>{type,value}</c>，图像降级成尺寸对象），
/// 避免同一份数据在文件与库里两套结构。
/// </summary>
public sealed class DatabaseWriteNode(IResultRecordStore? store = null) : IVisionNodeExecutor
{
    public string Type => "output.database";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var dataset = node.GetString("dataset", "results").Trim();
        if (dataset.Length == 0) throw new InvalidOperationException("output.database dataset must be non-empty.");
        if (dataset.Length > 96) throw new InvalidOperationException("output.database dataset must be at most 96 characters.");
        var maxBytes = ProtocolFraming.RequireRange(node.GetInt("maxPayloadBytes", 65536), 64, 4 * 1024 * 1024, "output.database maxPayloadBytes");

        var value = context.RequireValue("value");
        var recordKey = context.TryGetValue("key", out var connectedKey)
            ? connectedKey.Require<string>("key")
            : node.GetString("key", string.Empty);
        recordKey = string.IsNullOrWhiteSpace(recordKey) ? null : recordKey.Trim();
        if (recordKey is { Length: > 256 })
            throw new InvalidOperationException("output.database key must be at most 256 characters.");

        var serializable = value.Value switch
        {
            IVisionImage image => (object)new { image.Width, image.Height },
            IVisionFrameSet frames => new { frames.GroupId, frames.TimestampBasis, frames.TriggerSkewUs, frames.WithinTolerance, cameras = frames.Frames.Keys.Order().ToArray() },
            _ => value.Value
        };
        var payload = System.Text.Json.JsonSerializer.Serialize(new { type = value.Type.ToString(), value = serializable });
        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (payloadBytes > maxBytes)
            throw new InvalidOperationException($"output.database payload is {payloadBytes} bytes which exceeds maxPayloadBytes={maxBytes}; store a summary instead of the full object.");

        // 未注册存储时明确失败：静默丢弃会让"已入库"变成假象。
        var target = store ?? throw new InvalidOperationException(
            "output.database requires a host result store (IResultRecordStore). This host does not persist run results; " +
            "use output.exportResult for file output, or enable the host storage.");

        var record = new ResultRecord(
            Id: Guid.NewGuid().ToString("N"),
            Dataset: dataset,
            RecordKey: recordKey,
            PayloadJson: payload,
            RunId: context.RunId ?? string.Empty,
            WorkflowScope: context.WorkflowScope,
            NodeId: node.Id,
            CreatedAt: DateTimeOffset.UtcNow);

        try
        {
            await target.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 附带数据集与记录 id：写入失败时现场需要用它去核对是否半条落库。
            throw new InvalidOperationException($"output.database failed to append record {record.Id} to dataset '{dataset}': {ex.Message}", ex);
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("dataset", dataset),
            ("recordId", record.Id),
            ("recordKey", recordKey),
            ("payloadBytes", payloadBytes),
            ("valueType", value.Type.ToString()));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["recordId"] = VisionValue.String(record.Id),
                ["dataset"] = VisionValue.String(dataset),
                ["payloadBytes"] = VisionValue.Integer(payloadBytes),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["dataset"] = dataset,
                ["recordId"] = record.Id,
                ["recordKey"] = recordKey,
                ["payloadBytes"] = payloadBytes,
                ["diagnostics"] = diagnostics
            });
    }
}
