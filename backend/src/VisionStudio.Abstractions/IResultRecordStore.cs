namespace VisionStudio.Abstractions;

/// <summary>
/// 一条需要落库的运行结果（2026-10-26 · 缺口分析 P1-12）。
///
/// 为什么是"记录"而不是"表结构"：节点不应该知道业务表长什么样。宿主负责真正的存储
/// （本仓库是 SQLite 的 <c>result_records</c> 表），节点只声明"把这次结果追加到某个数据集"。
/// 这样换存储（SQLite → PostgreSQL → 时序库）不需要改配方。
/// </summary>
public sealed record ResultRecord(
    /// <summary>记录主键，由节点生成，便于重试时判重。</summary>
    string Id,
    /// <summary>逻辑数据集名（配方里配置，例如 "caliper-width"、"ng-parts"）。</summary>
    string Dataset,
    /// <summary>可选业务键（产品序列号、批次号）；用于按件查询。</summary>
    string? RecordKey,
    /// <summary>负载 JSON（含类型信息的 <c>{type,value}</c>，与导出节点的 JSON 形态一致）。</summary>
    string PayloadJson,
    string RunId,
    /// <summary>工作流编译计划标识（同一份内容复用同一标识）；宿主可选记录。</summary>
    string? WorkflowScope,
    string NodeId,
    DateTimeOffset CreatedAt)
{
    /// <summary>负载字节数（UTF-8），用于体积统计与配额。</summary>
    public int PayloadBytes => System.Text.Encoding.UTF8.GetByteCount(PayloadJson);
}

/// <summary>
/// 结果落库契约。宿主实现可用任意存储；未注册实现时，<c>output.database</c> 节点必须**拒绝执行**
/// 而不是静默丢弃结果——静默丢弃会让"已入库"的假设在事后对账时才暴露。
/// </summary>
public interface IResultRecordStore
{
    /// <summary>追加一条记录。实现需保证单条写入的原子性（要么整条落库，要么失败）。</summary>
    Task AppendAsync(ResultRecord record, CancellationToken cancellationToken = default);
}
