using System.Net;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class SaveImageNode : IVisionNodeExecutor
{
    public string Type => "output.saveImage";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = RequirePath(node);
        var image = context.RequireImage("image");
        EnsureDirectory(path);
        var overwrite = node.GetBool("overwrite", false);
        if (!overwrite && File.Exists(path)) throw new IOException($"Output file already exists: '{path}'. Set overwrite=true to replace it.");
        var tempPath = CreateTempPath(path);
        try
        {
            if (!Cv2.ImWrite(tempPath, image.Mat)) throw new IOException($"OpenCV could not encode image for '{path}'. Use a supported image extension.");
            cancellationToken.ThrowIfCancellationRequested();
            Commit(tempPath, path, overwrite);
        }
        finally { TryDelete(tempPath); }
        var info = new FileInfo(path);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(path), ["bytesWritten"] = VisionValue.Integer(info.Length) },
            new Dictionary<string, object?> { ["path"] = path, ["bytesWritten"] = info.Length, ["width"] = image.Width, ["height"] = image.Height }));
    }

    internal static string RequirePath(NodeDefinition node)
    {
        var path = node.GetString("path", string.Empty).Trim();
        if (path.Length == 0) throw new InvalidOperationException("Output path is required.");
        return Path.GetFullPath(path);
    }

    internal static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    internal static string CreateTempPath(string path)
    {
        var extension = Path.GetExtension(path);
        var stem = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
        return $"{stem}.{Guid.NewGuid():N}.tmp{extension}";
    }

    internal static void Commit(string tempPath, string path, bool overwrite) => File.Move(tempPath, path, overwrite);
    internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* preserve the original failure */ } }
}

public sealed class ExportResultNode : IVisionNodeExecutor
{
    public string Type => "output.exportResult";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var path = SaveImageNode.RequirePath(node);
        var format = node.GetString("format", "json").Trim().ToLowerInvariant();
        var overwrite = node.GetBool("overwrite", false);
        if (!overwrite && File.Exists(path)) throw new IOException($"Output file already exists: '{path}'. Set overwrite=true to replace it.");
        var value = context.RequireValue("value");
        var safeValue = ToSerializableValue(value);
        var contents = format switch
        {
            "json" => JsonSerializer.Serialize(new { type = value.Type.ToString(), value = safeValue }, new JsonSerializerOptions { WriteIndented = true }),
            "csv" => "type,value\r\n" + Csv(value.Type.ToString()) + "," + Csv(JsonSerializer.Serialize(safeValue)) + "\r\n",
            _ => throw new InvalidOperationException("output.exportResult format must be 'json' or 'csv'.")
        };
        SaveImageNode.EnsureDirectory(path);
        var tempPath = SaveImageNode.CreateTempPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SaveImageNode.Commit(tempPath, path, overwrite);
        }
        finally { SaveImageNode.TryDelete(tempPath); }
        var bytes = Encoding.UTF8.GetByteCount(contents);
        return new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["path"] = VisionValue.String(path), ["bytesWritten"] = VisionValue.Integer(bytes)
        }, new Dictionary<string, object?> { ["path"] = path, ["format"] = format, ["bytesWritten"] = bytes });
    }

    private static object? ToSerializableValue(VisionValue value) => value.Value switch
    {
        IVisionImage image => new { image.Width, image.Height },
        IVisionFrameSet frames => new { frames.GroupId, frames.TimestampBasis, frames.TriggerSkewUs, frames.WithinTolerance, cameras = frames.Frames.Keys.Order().ToArray() },
        null => null,
        _ => value.Value
    };

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>HTTP request with an explicit linked timeout; non-2xx responses fail the workflow.</summary>
public sealed class HttpRequestNode(HttpClient? httpClient = null) : IVisionNodeExecutor
{
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = httpClient ?? SharedClient;
    public string Type => "output.httpRequest";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var uriText = node.GetString("url", string.Empty).Trim();
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("HTTP request URL must be an absolute http or https URL.");
        var timeoutMs = node.GetInt("timeoutMs", 10000);
        if (timeoutMs is < 1 or > 120000) throw new InvalidOperationException("HTTP timeoutMs must be between 1 and 120000.");
        var maxResponseBytes = node.GetInt("maxResponseBytes", 1048576);
        if (maxResponseBytes is < 1 or > 67108864) throw new InvalidOperationException("HTTP maxResponseBytes must be between 1 and 67108864.");
        var methodText = node.GetString("method", "GET").Trim().ToUpperInvariant();
        if (methodText is not ("GET" or "POST" or "PUT" or "DELETE" or "HEAD")) throw new InvalidOperationException($"Unsupported HTTP method '{methodText}'.");
        var method = new HttpMethod(methodText);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var request = new HttpRequestMessage(method, uri);
        var body = node.GetString("body", string.Empty);
        if (method != HttpMethod.Get && method != HttpMethod.Head && body.Length > 0)
            request.Content = new StringContent(body, Encoding.UTF8, node.GetString("contentType", "application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP request returned {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
        var responseBody = await ReadBoundedBodyAsync(response.Content, maxResponseBytes, timeout.Token).ConfigureAwait(false);
        return new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["statusCode"] = VisionValue.Integer((int)response.StatusCode),
            ["body"] = VisionValue.String(responseBody),
            ["success"] = VisionValue.Boolean(true)
        }, new Dictionary<string, object?> { ["statusCode"] = (int)response.StatusCode, ["reasonPhrase"] = response.ReasonPhrase, ["responseLength"] = responseBody.Length });
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes) throw new InvalidOperationException($"HTTP response exceeds maxResponseBytes={maxBytes}.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > maxBytes) throw new InvalidOperationException($"HTTP response exceeds maxResponseBytes={maxBytes}.");
            bytes.Write(buffer, 0, count);
        }
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        var encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        using var reader = new StreamReader(new MemoryStream(bytes.ToArray()), encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
