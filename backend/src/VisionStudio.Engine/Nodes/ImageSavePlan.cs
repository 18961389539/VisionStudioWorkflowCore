using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VisionStudio.Engine.Nodes;

/// <summary>Resolve one immutable save identity before encoding or retrying any disk operation.</summary>
internal sealed record ImageSavePlan(string RequestedPath, string TargetPath, string Codec, string ConflictPolicy,
    string NamingMode, string ResultMode, string Disposition, bool ShouldSave, string RunId, bool HostRunId,
    string SaveId, DateTimeOffset Timestamp)
{
    private static readonly Regex Tokens = new(@"\{([a-zA-Z][a-zA-Z0-9]*)\}", RegexOptions.CultureInvariant);
    private const string PortableInvalid = "<>:\"/\\|?*";

    internal static void ValidateConnections(NodeDefinition node, IReadOnlySet<string> connectedPorts)
    {
        var resultMode = Choice(node, "resultMode", "all", "all", "byResult", "okOnly", "ngOnly");
        if (resultMode != "all" && !connectedPorts.Contains("pass")) throw new InvalidOperationException($"Node '{node.Id}': OK/NG 分类或筛选保存需要连接 pass。");
        if (!node.GetString("namingMode", "fixed").Equals("template", StringComparison.OrdinalIgnoreCase)) return;
        var template = node.GetString("filenameTemplate", "{timestamp}_{saveId}").Trim();
        ValidateTemplate(template);
        var names = Tokens.Matches(template).Select(match => match.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        if (names.Contains("result") && !connectedPorts.Contains("pass")) throw new InvalidOperationException($"Node '{node.Id}': 模板使用 {{result}} 时需要连接 pass。");
        if (names.Contains("sequence") && !connectedPorts.Contains("sequence")) throw new InvalidOperationException($"Node '{node.Id}': 模板使用 {{sequence}} 时需要连接序号输入。");
        if (names.Contains("partid") && !connectedPorts.Contains("partId") && string.IsNullOrWhiteSpace(node.GetString("partId", "")))
            throw new InvalidOperationException($"Node '{node.Id}': 模板使用 {{partId}} 时需要连接或配置产品编号。");
    }

    internal static ImageSavePlan Create(NodeExecutionContext context, NodeDefinition node)
    {
        var rawPath = context.TryGetValue("path", out var pathInput) ? pathInput.Require<string>("path") : node.GetString("path", "artifacts/output.png");
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException("保存路径不能为空，且必须是有效文件路径。");
        var requestedPath = Path.GetFullPath(rawPath.Trim());
        ValidateFileName(Path.GetFileName(requestedPath));
        var requestedFormat = Choice(node, "format", "auto", "auto", "png", "jpeg", "bmp");
        var extension = Path.GetExtension(requestedPath);
        var codec = requestedFormat == "auto" ? extension.ToLowerInvariant() : requestedFormat == "jpeg" ? ".jpg" : "." + requestedFormat;
        if (codec is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".ppm" or ".pgm" or ".pbm"))
            throw new InvalidOperationException("保存图像需要有效的图像扩展名，例如 .png、.jpg、.bmp、.tif。");
        if (requestedFormat != "auto" && !codec.Equals(extension, StringComparison.OrdinalIgnoreCase)
            && !(codec == ".jpg" && extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("文件扩展名必须与指定格式一致，或将格式设为按扩展名选择。");

        // Missing new fields mean a historical recipe. New nodes receive timestamp/suffix from the catalog.
        var naming = Choice(node, "namingMode", "fixed", "fixed", "timestamp", "template");
        var policy = node.Parameters?.ContainsKey("conflictPolicy") == true ? Choice(node, "conflictPolicy", "fail", "fail", "overwrite", "suffix", "skip")
            : node.GetBool("overwrite", false) ? "overwrite" : "fail";
        var resultMode = Choice(node, "resultMode", "all", "all", "byResult", "okOnly", "ngOnly");
        bool? pass = context.TryGetValue("pass", out var passInput) ? passInput.Require<bool>("pass") : null;
        if (resultMode != "all" && pass is null) throw new InvalidOperationException("OK/NG 分类或筛选保存需要连接 pass 判定结果。");
        var disposition = pass is null ? "UNCLASSIFIED" : pass.Value ? "OK" : "NG";
        var shouldSave = resultMode switch { "okOnly" => pass == true, "ngOnly" => pass == false, _ => true };
        var now = DateTimeOffset.Now;
        var saveId = Guid.NewGuid().ToString("N");
        var hostRunId = !string.IsNullOrWhiteSpace(context.RunId);
        var runId = hostRunId ? context.RunId! : saveId;
        if (!shouldSave) return new(requestedPath, requestedPath, codec, policy, naming, resultMode, disposition, false, runId, hostRunId, saveId, now);

        var directory = Path.GetDirectoryName(requestedPath)!;
        if (node.GetBool("splitByDate", false)) directory = Path.Combine(directory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (resultMode != "all") directory = Path.Combine(directory, disposition);
        var stem = Path.GetFileNameWithoutExtension(requestedPath);
        var timestamp = now.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);
        if (naming == "timestamp") stem += "_" + timestamp + "_" + saveId[..8];
        else if (naming == "template")
        {
            var template = node.GetString("filenameTemplate", "{timestamp}_{saveId}").Trim();
            ValidateTemplate(template);
            stem = Tokens.Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
            {
                "timestamp" => timestamp,
                "date" => now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                "runid" => Component(runId, "运行 ID"),
                "saveid" => saveId,
                "nodeid" => Component(node.Id, "节点 ID"),
                "result" => pass is null ? throw new InvalidOperationException("模板使用 {result} 时需要连接 pass。") : disposition,
                "partid" => Component(context.TryGetValue("partId", out var part) ? part.Require<string>("partId") : node.GetString("partId", ""), "产品编号"),
                "sequence" => Sequence(context),
                _ => throw new InvalidOperationException($"未知文件名占位符 {match.Value}。支持 timestamp/date/runId/saveId/nodeId/result/partId/sequence。")
            });
        }
        var fileName = stem + extension;
        ValidateFileName(fileName);
        return new(requestedPath, Path.Combine(directory, fileName), codec, policy, naming, resultMode, disposition, true, runId, hostRunId, saveId, now);
    }

    internal string AlternatePath()
    {
        var extension = Path.GetExtension(TargetPath);
        var stem = Path.GetFileNameWithoutExtension(TargetPath);
        var available = 240 - extension.Length - 33;
        if (stem.Length > available)
        {
            // Do not split a Unicode surrogate pair when reserving room for the collision suffix.
            if (char.IsHighSurrogate(stem[available - 1])) available--;
            stem = stem[..available];
        }
        return Path.Combine(Path.GetDirectoryName(TargetPath)!, stem + "_" + Guid.NewGuid().ToString("N") + extension);
    }

    private static void ValidateTemplate(string template)
    {
        if (template.Length == 0 || template.Length > 512 || template.Any(ch => IsInvalid(ch)))
            throw new InvalidOperationException("文件名模板不能为空，最多 512 字符，不包含目录分隔符或非法文件名字符；扩展名沿用保存路径。");
        if (Tokens.Replace(template, "").IndexOfAny(['{', '}']) >= 0) throw new InvalidOperationException("文件名模板括号不完整，不支持自定义格式后缀。");
        foreach (Match match in Tokens.Matches(template))
            if (match.Groups[1].Value.ToLowerInvariant() is not ("timestamp" or "date" or "runid" or "saveid" or "nodeid" or "result" or "partid" or "sequence"))
                throw new InvalidOperationException($"未知占位符 {match.Value}。支持 timestamp/date/runId/saveId/nodeId/result/partId/sequence。");
    }

    private static string Sequence(NodeExecutionContext context)
    {
        if (!context.TryGetValue("sequence", out var value)) throw new InvalidOperationException("模板使用 {sequence} 时需要连接非负整数序号；序号来自流程输入，不由保存节点自行计数。");
        var sequence = value.Require<long>("sequence");
        if (sequence < 0) throw new InvalidOperationException("保存序号必须是非负整数。");
        return sequence.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string Component(string raw, string label)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value.Length > 128) throw new InvalidOperationException($"{label} 须为 1..128 字符；请接线或配置该值。");
        var original = value;
        value = new string(value.Select(ch => IsInvalid(ch) ? '_' : ch).ToArray()).TrimEnd('.', ' ');
        if (value.Length == 0) throw new InvalidOperationException($"{label} 不能仅包含点或空白。");
        if (Reserved(value)) value = "_" + value;
        if (value != original) value += "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..8].ToLowerInvariant();
        return value;
    }

    private static bool IsInvalid(char ch) => ch < 32 || PortableInvalid.Contains(ch) || Path.GetInvalidFileNameChars().Contains(ch);
    private static bool Reserved(string fileName)
    {
        var stem = fileName.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }

    private static void ValidateFileName(string name)
    {
        if (name.Length is < 1 or > 240 || name.Any(ch => IsInvalid(ch)) || name.EndsWith('.') || name.EndsWith(' ') || Reserved(name))
            throw new InvalidOperationException("输出文件名无效或超过 240 字符；请缩短模板，不使用目录分隔符、保留设备名或末尾点/空格。");
    }

    private static string Choice(NodeDefinition node, string name, string fallback, params string[] options)
    {
        var value = node.GetString(name, fallback).Trim();
        return options.FirstOrDefault(option => option.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{name} 必须为 {string.Join('/', options)}。");
    }
}
