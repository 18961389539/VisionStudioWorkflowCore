using System.Collections.Concurrent;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 一个节点类型的耗时分布（2026-10-29）。
/// </summary>
/// <param name="NodeType">节点类型。</param>
/// <param name="Samples">窗口内的样本数（最多 <see cref="NodeDurationStatistics.DefaultCapacity"/>）。</param>
/// <param name="P50Ms">中位耗时。</param>
/// <param name="P95Ms">95 分位耗时——选 <c>__timeoutMs</c> 应该看这个，而不是平均值。</param>
/// <param name="MaxMs">窗口内最大耗时。</param>
/// <param name="ConfiguredTimeoutMs">该类型节点最近一次执行时携带的 <c>__timeoutMs</c>；未配置为 null。</param>
/// <param name="SuggestedTimeoutMs">按 P95 推出的建议超时；样本不足 20 条时为 null（见下方说明）。</param>
/// <param name="TimeoutCount">窗口内真实触发过节点超时的次数。</param>
public sealed record NodeDurationStatistic(
    string NodeType,
    int Samples,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    int? ConfiguredTimeoutMs,
    int? SuggestedTimeoutMs,
    int TimeoutCount)
{
    /// <summary>
    /// 当前配置偏紧：P95 已经吃掉配置值的 80% 以上。按正态的尾部走，下一次抖动就会误杀。
    /// 这不是"一定出错"，而是"该检查了"——节拍在有负载波动时会偶发 node_timeout。
    /// </summary>
    public bool TimeoutTooTight => ConfiguredTimeoutMs is { } configured && configured > 0 && P95Ms > configured * 0.8;

    /// <summary>窗口内触发过超时：配置值已经被实际打穿过，属于确凿证据而不是推断。</summary>
    public bool HasTimedOut => TimeoutCount > 0;
}

/// <summary>
/// 按节点类型收集执行耗时，供诊断端点回答一个具体问题：**这个节点该配多少 <c>__timeoutMs</c>**。
///
/// 为什么需要它：<c>__timeoutMs</c> 的语义（未配置即不设限）是对的，但工程师没有数据可依——
/// 只能凭感觉填一个数，填小了两周后偶发停机，填大了等于没有保护。已有的 trace 有单次耗时，
/// 但要人工翻样本自己算分位数，实际没人做。
///
/// 容量与语义：
/// - 每个节点类型只保留最近 <see cref="DefaultCapacity"/> 次样本（环形覆盖），常驻内存可忽略；
/// - 分位数用最近秩（nearest-rank），与 <c>DatasetValidation</c>/<c>CameraSynchronization</c> 口径一致；
/// - **不落盘**：这是运行期观测值，重启清零。落盘会让"上一批工艺的耗时"混进当前建议里。
/// </summary>
public sealed class NodeDurationStatistics
{
    /// <summary>每个节点类型的样本窗口大小。</summary>
    public const int DefaultCapacity = 256;

    /// <summary>
    /// 给出建议超时所需的最小样本数。少于这个数时 P95 只是噪声：
    /// 5 个样本的"95 分位"就是最大值，据此推建议会把偶发尖峰固化成配置。
    /// </summary>
    public const int MinSamplesForSuggestion = 20;

    /// <summary>建议值 = P95 × 该系数。1.5 倍是"留出一次抖动"的经验余量。</summary>
    private const double SuggestionHeadroom = 1.5;

    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public NodeDurationStatistics(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>记录一次节点执行。耗时非法（NaN/负数）时归零，不让脏样本污染分位数。</summary>
    public void Record(string nodeType, double elapsedMs, int configuredTimeoutMs = 0, bool timedOut = false)
    {
        if (string.IsNullOrWhiteSpace(nodeType)) return;
        if (!double.IsFinite(elapsedMs) || elapsedMs < 0) elapsedMs = 0;
        _buckets.GetOrAdd(nodeType, _ => new Bucket(_capacity)).Add(elapsedMs, configuredTimeoutMs, timedOut);
    }

    /// <summary>取全部节点类型的统计，按 P95 降序（最慢的在最前面，正是需要关注的那批）。</summary>
    public IReadOnlyList<NodeDurationStatistic> Snapshot()
    {
        var statistics = new List<NodeDurationStatistic>(_buckets.Count);
        foreach (var (nodeType, bucket) in _buckets)
        {
            var (sorted, configuredTimeoutMs, timeoutCount) = bucket.Snapshot();
            if (sorted.Length == 0) continue;

            var p95 = Percentile(sorted, 0.95);
            statistics.Add(new NodeDurationStatistic(
                nodeType,
                sorted.Length,
                Math.Round(Percentile(sorted, 0.50), 2),
                Math.Round(p95, 2),
                Math.Round(sorted[^1], 2),
                configuredTimeoutMs > 0 ? configuredTimeoutMs : null,
                sorted.Length >= MinSamplesForSuggestion ? Suggest(p95) : null,
                timeoutCount));
        }

        statistics.Sort(static (left, right) =>
        {
            var byP95 = right.P95Ms.CompareTo(left.P95Ms);
            return byP95 != 0 ? byP95 : string.CompareOrdinal(left.NodeType, right.NodeType);
        });
        return statistics;
    }

    /// <summary>清空统计（测试用；也可在切换配方后由宿主调用）。</summary>
    public void Clear() => _buckets.Clear();

    /// <summary>P95 × 余量，向上取整到 100 ms，再钳进 [100, 当前上限]。</summary>
    private static int Suggest(double p95Ms)
    {
        var target = Math.Ceiling(p95Ms * SuggestionHeadroom / 100d) * 100d;
        return (int)Math.Clamp(target, 100d, NodeTimeoutPolicy.CeilingMs);
    }

    /// <summary>最近秩分位数（对已排序数组）。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>环形样本缓冲：写路径只做一次数组赋值，读路径才排序。</summary>
    private sealed class Bucket(int capacity)
    {
        private readonly double[] _samples = new double[capacity];
        private readonly object _gate = new();
        private int _next;
        private int _count;
        private int _timeoutCount;
        private int _configuredTimeoutMs;

        public void Add(double elapsedMs, int configuredTimeoutMs, bool timedOut)
        {
            lock (_gate)
            {
                _samples[_next] = elapsedMs;
                _next = (_next + 1) % _samples.Length;
                if (_count < _samples.Length) _count++;
                if (timedOut) _timeoutCount++;
                // 记住最近一次**非零**配置：节点被改回不设限时不要丢掉"曾经配过多少"这个线索。
                if (configuredTimeoutMs > 0) _configuredTimeoutMs = configuredTimeoutMs;
            }
        }

        /// <summary>
        /// 取排序后的样本副本。未填满时有效元素在 <c>[0, _count)</c>（此时 _next == _count），
        /// 填满后全部元素有效——两种情况下前 _count 个元素就是完整样本集。
        /// </summary>
        public (double[] Sorted, int ConfiguredTimeoutMs, int TimeoutCount) Snapshot()
        {
            lock (_gate)
            {
                var copy = new double[_count];
                Array.Copy(_samples, copy, _count);
                Array.Sort(copy);
                return (copy, _configuredTimeoutMs, _timeoutCount);
            }
        }
    }
}
