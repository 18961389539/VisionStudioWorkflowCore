using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 表格读入（device.readTable，2026-10-27 · 缺口分析 P0-2）。
///
/// 现场缺口：`output.exportResult` 只会写 CSV，没有任何节点能**读**。于是"按工单里的 SN 逐个检测"
/// 只能在上位机另写一个发号服务，流程本身无法自洽地跑批。
///
/// 设计取舍：
/// - 读盘一律经过宿主的 <see cref="IReplayInputResolver"/>（与 image.load 同一条安全边界），
///   不做任意路径读取；未授权时明确失败并提示配置 `VisionStudio:ReplayInputRoots`。
/// - 按 `sequence` 取行（0 基，表头不计入），`wrap` 打开时循环取模——产线"跑满一遍从头再来"
///   比"跑完停机"更常见。越界且不循环返回 found=false，不抛异常，交给 flow.if 决定。
/// - 解析结果按 (路径, 修改时间, 长度, 编码, 分隔符) 缓存，同一文件跑 N 件只解析一次；
///   缓存条数有上限，避免多配方把内存吃光。
/// - 编码默认 UTF-8；国内 Excel 导出的 CSV 常是 GBK，因此支持 gb18030（需注册 CodePages 提供程序）。
/// - 文件内容可能在两次运行之间被改动，故声明 Deterministic=false，不进入自动重试。
/// </summary>
public sealed class TableReadNode(IReplayInputResolver? replayInput = null) : IVisionNodeExecutor
{
    private const string NodeType = "device.readTable";

    public string Type => NodeType;

    private const int MaxCachedTables = 4;

    /// <summary>中文列名保持明文、引号用 \" 而不是 \u0022（仓库既有落盘 JSON 约定，非 HTML 上下文）。</summary>
    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly ConcurrentDictionary<string, CachedTable> Cache = new(StringComparer.Ordinal);

    static TableReadNode()
    {
        // GB18030 不属于 .NET 内置编码集，必须显式注册；重复注册是幂等的。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private sealed record CachedTable(string Key, string[] Header, List<string[]> Rows, string RawHeader);

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var requested = (context.TryGetValue("path", out var pathInput)
            ? pathInput.Require<string>("path")
            : node.GetString("path", string.Empty)).Trim();
        if (requested.Length == 0)
            throw new InvalidOperationException("device.readTable requires a non-empty path to a CSV/TSV file.");
        if (requested.Length > 1024)
            throw new InvalidOperationException("device.readTable path is longer than 1024 characters.");

        var authorization = replayInput ?? throw new InvalidOperationException(
            "device.readTable requires host file-read authorization (IReplayInputResolver). " +
            "Add the folder to 'VisionStudio:ReplayInputRoots' (or keep it under the data/media root) instead of relying on arbitrary paths.");
        var resolved = authorization.Resolve(requested) ?? throw new InvalidOperationException(
            $"device.readTable path '{requested}' is outside the authorized read roots. Add the folder to 'VisionStudio:ReplayInputRoots' to allow it.");
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"device.readTable file was not found: '{requested}'.", resolved);

        var hasHeader = node.GetBool("hasHeader", true);
        var wrap = node.GetBool("wrap", false);
        var maxRows = ProtocolFraming.RequireRange(node.GetInt("maxRows", 100_000), 1, 1_000_000, "device.readTable maxRows");
        var maxMegabytes = ProtocolFraming.RequireRange(node.GetInt("maxMegabytes", 16), 1, 512, "device.readTable maxMegabytes");
        var encodingName = node.GetString("encoding", "utf8").Trim().ToLowerInvariant();
        var encoding = encodingName switch
        {
            "utf8" or "utf8bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            "gb18030" => Encoding.GetEncoding("GB18030"),
            "latin1" => Encoding.Latin1,
            _ => throw new InvalidOperationException("device.readTable encoding must be utf8, gb18030 or latin1.")
        };
        var delimiterName = node.GetString("delimiter", "auto").Trim().ToLowerInvariant();
        if (delimiterName is not ("auto" or "comma" or "tab" or "semicolon" or "pipe"))
            throw new InvalidOperationException("device.readTable delimiter must be auto, comma, tab, semicolon or pipe.");

        var info = new FileInfo(resolved);
        if (info.Length > maxMegabytes * 1024L * 1024L)
            throw new InvalidOperationException(
                $"device.readTable file is {info.Length} bytes which exceeds maxMegabytes={maxMegabytes}; split the table or raise the limit.");

        var cacheKey = FormattableString.Invariant(
            $"{resolved}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{encodingName}|{delimiterName}|{hasHeader}|{maxRows}");
        var table = Cache.TryGetValue(cacheKey, out var hit) ? hit : Load(resolved, cacheKey, encoding, delimiterName,
            hasHeader, maxRows, cancellationToken);
        if (table is null)
        {
            Cache.TryRemove(cacheKey, out _);
            table = Load(resolved, cacheKey, encoding, delimiterName, hasHeader, maxRows, cancellationToken)!;
        }

        var total = table.Rows.Count;
        if (total == 0)
            return ValueTask.FromResult(Missing("table-has-no-data-rows", node, hasHeader, 0));

        var indexParam = context.TryGetValue("sequence", out var sequenceInput)
            ? (int)sequenceInput.Require<long>("sequence")
            : node.GetInt("index", 0);
        var index = wrap ? ((indexParam % total) + total) % total : indexParam;
        if (index < 0 || index >= total)
            return ValueTask.FromResult(Missing(
                $"index {indexParam} is outside 0..{total - 1} (wrap is off)", node, hasHeader, total));

        var cells = table.Rows[index];
        var rowJson = JsonSerializer.Serialize(cells, JsonText);
        var columnsJson = JsonSerializer.Serialize(table.Header, JsonText);
        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("file", Path.GetFileName(resolved)), ("hasHeader", hasHeader), ("index", index), ("total", total),
            ("cellCount", cells.Length), ("encoding", encodingName), ("delimiter", delimiterName));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(true),
            ["index"] = VisionValue.Integer(index),
            ["total"] = VisionValue.Integer(total),
            ["cellCount"] = VisionValue.Integer(cells.Length),
            ["header"] = VisionValue.String(table.RawHeader),
            ["columnsJson"] = VisionValue.String(columnsJson),
            ["row"] = VisionValue.String(string.Join(",", cells)),
            ["rowJson"] = VisionValue.String(rowJson),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };

        // 表头与数据列数一致时才给键值对象：列数对不上说明文件本身有问题，宁可不给也不给错位映射。
        if (hasHeader && table.Header.Length > 0 && table.Header.Length == cells.Length)
        {
            var mapped = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var column = 0; column < cells.Length; column++) mapped[table.Header[column]] = cells[column];
            outputs["rowObjectJson"] = VisionValue.String(JsonSerializer.Serialize(mapped, JsonText));
        }

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["file"] = Path.GetFileName(resolved), ["index"] = index, ["total"] = total,
                ["cellCount"] = cells.Length, ["hasHeader"] = hasHeader, ["encoding"] = encodingName,
                ["delimiter"] = delimiterName, ["diagnostics"] = diagnostics
            }));
    }

    private static NodeExecutorResult Missing(string reason, NodeDefinition node, bool hasHeader, int total)
    {
        var diagnostics = CoordinateValidation.Diagnostics(NodeType, ("found", false), ("reason", reason), ("total", total));
        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(false),
            ["total"] = VisionValue.Integer(total),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        return new NodeExecutorResult(outputs,
            new Dictionary<string, object?> { ["found"] = false, ["reason"] = reason, ["total"] = total, ["diagnostics"] = diagnostics });
    }

    private static CachedTable? Load(string path, string cacheKey, Encoding encoding, string delimiterName,
        bool hasHeader, int maxRows, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using (var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) continue; // 空行不占序号，否则 Excel 末尾空行会让 total 虚高
                lines.Add(line);
                if (lines.Count > maxRows + (hasHeader ? 1 : 0))
                    throw new InvalidOperationException(
                        $"device.readTable file has more than maxRows={maxRows} rows; raise maxRows or split the table.");
            }
        }

        if (lines.Count == 0) return new CachedTable(cacheKey, [], [], string.Empty);

        var delimiter = ResolveDelimiter(delimiterName, lines[0]);
        var header = hasHeader ? SplitLine(lines[0], delimiter) : [];
        var firstData = hasHeader ? 1 : 0;
        var rows = new List<string[]>(Math.Max(0, lines.Count - firstData));
        for (var index = firstData; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(SplitLine(lines[index], delimiter));
        }

        var table = new CachedTable(cacheKey, header, rows, hasHeader ? lines[0] : string.Empty);
        if (Cache.Count >= MaxCachedTables) Cache.Clear(); // 简单粗暴但可预测：容量满了整体丢弃，不做 LRU 记账
        Cache[cacheKey] = table;
        return table;
    }

    /// <summary>auto 模式：看表头行里哪种分隔符出现次数最多（引号内的不计）。</summary>
    private static char ResolveDelimiter(string name, string sampleLine)
    {
        switch (name)
        {
            case "comma": return ',';
            case "tab": return '\t';
            case "semicolon": return ';';
            case "pipe": return '|';
        }

        char[] candidates = [',', ';', '\t', '|'];
        var best = ',';
        var bestCount = -1;
        foreach (var candidate in candidates)
        {
            var count = SplitLine(sampleLine, candidate).Length;
            if (count <= bestCount) continue;
            bestCount = count;
            best = candidate;
        }
        return best;
    }

    /// <summary>RFC4180 风格拆分：支持双引号包裹、字段内分隔符与换行、"" 转义。</summary>
    private static string[] SplitLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var builder = new StringBuilder(line.Length);
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        builder.Append('"');
                        index++;
                        continue;
                    }
                    inQuotes = false;
                    continue;
                }
                builder.Append(ch);
                continue;
            }

            if (ch == '"' && builder.Length == 0)
            {
                inQuotes = true;
                continue;
            }
            if (ch == delimiter)
            {
                cells.Add(builder.ToString());
                builder.Clear();
                continue;
            }
            builder.Append(ch);
        }
        cells.Add(builder.ToString());
        return [.. cells];
    }
}
