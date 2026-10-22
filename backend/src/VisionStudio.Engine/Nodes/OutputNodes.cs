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
        => ImageFileWriter.ExecuteAsync(context, node, cancellationToken);

    internal static string RequirePath(NodeDefinition node, string fallback)
    {
        var path = node.GetString("path", fallback).Trim();
        if (path.Length == 0) throw new InvalidOperationException("Output path is required.");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new InvalidOperationException("Output path contains invalid characters.");
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
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ResultFileWriter.ExecuteAsync(context, node, cancellationToken);
}

/// <summary>HTTP request with linked timeout, optional retries, and status diagnostics; non-2xx fails after retries.</summary>
public sealed class HttpRequestNode(HttpClient? httpClient = null) : IVisionNodeExecutor
{
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = httpClient ?? SharedClient;
    public string Type => "output.httpRequest";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uriText = (context.TryGetValue("url", out var urlInput) ? urlInput.Require<string>("url") : node.GetString("url", string.Empty)).Trim();
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("HTTP request URL must be an absolute http or https URL.");
        var timeoutMs = Integer(node, "timeoutMs", 10000, 1, 120000);
        var maxResponseBytes = Integer(node, "maxResponseBytes", 1048576, 1, 67108864);
        var methodText = node.GetString("method", "GET").Trim().ToUpperInvariant();
        if (methodText is not ("GET" or "POST" or "PUT" or "DELETE" or "HEAD")) throw new InvalidOperationException($"Unsupported HTTP method '{methodText}'.");
        var requestedRetries = node.Parameters?.ContainsKey("retries") == true ? Integer(node, "retries", 0, 0, 5)
            : NodeCompletionGuards.RetryAttempts(NodeCompletionGuards.RetryPolicy(node)) - 1;
        var retryDelayMs = Integer(node, "retryDelayMs", 250, 0, 5000);
        var method = new HttpMethod(methodText);
        var readOnly = method == HttpMethod.Get || method == HttpMethod.Head;
        var idempotencyKey = (context.TryGetValue("idempotencyKey", out var keyInput) ? keyInput.Require<string>("idempotencyKey") : node.GetString("idempotencyKey", "")).Trim();
        if (idempotencyKey.Length > 256 || idempotencyKey.Any(ch => ch < 33 || ch > 126))
            throw new InvalidOperationException("幂等键须为最多 256 个可打印 ASCII 字符，不含空格和换行；可接入每件产品的唯一编号。");
        var retryWrites = node.GetBool("retryWriteRequests", false);
        if (!readOnly && retryWrites && requestedRetries > 0 && idempotencyKey.Length == 0)
            throw new InvalidOperationException("写请求重试需要稳定的幂等键，请连接 idempotencyKey 或配置该参数；服务端也须支持此幂等键。");
        var retries = readOnly || retryWrites && idempotencyKey.Length > 0 ? requestedRetries : 0;
        var retrySuppressed = retries != requestedRetries;
        var hasBodyInput = context.TryGetValue("body", out var bodyInput);
        var hasValueInput = context.TryGetValue("value", out var valueInput);
        if (hasBodyInput && hasValueInput) throw new InvalidOperationException("HTTP 正文只能连接 body 或 value 之一：body 发送原文，value 序列化为 JSON。");
        if (readOnly && (hasBodyInput || hasValueInput)) throw new InvalidOperationException("GET/HEAD 不发送正文，请断开正文输入或改用 POST/PUT/DELETE。");
        var body = readOnly ? "" : hasBodyInput ? bodyInput!.Require<string>("body") : node.GetString("body", string.Empty);
        var contentType = node.GetString("contentType", "application/json");
        if (hasValueInput)
        {
            if (valueInput!.Value is IVisionImage or IVisionDomainImage or IVisionFrameSet || valueInput.Type == VisionDataType.Control)
                throw new InvalidOperationException("HTTP value 输入仅接受可序列化的检测或测量数据；图像请通过专用上传流程处理。");
            var payload = valueInput.Value is IReadOnlyList<VisionContour> contours
                ? (object)contours.Select(contour => contour with { NativeSnapshot = null }).ToArray() : valueInput.Value;
            body = JsonSerializer.Serialize(payload, new JsonSerializerOptions { MaxDepth = 64 });
            contentType = "application/json";
        }
        var requestBytes = Encoding.UTF8.GetByteCount(body);
        if (!readOnly && requestBytes > Integer(node, "maxRequestBytes", 1048576, 1, 67108864))
            throw new InvalidOperationException("HTTP 请求正文超过 maxRequestBytes，未发送请求。");
        HttpResponseMessage? lastResponse = null;
        string? lastBody = null;
        Exception? lastError = null;
        var attempts = 0;
        var responseComplete = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            for (var attempt = 0; attempt <= retries; attempt++)
            {
                attempts = attempt + 1;
                responseComplete = false; lastBody = null;
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMs);
                try
                {
                    using var request = new HttpRequestMessage(method, uri);
                    if (idempotencyKey.Length > 0) request.Headers.Add("Idempotency-Key", idempotencyKey);
                    if (method != HttpMethod.Get && method != HttpMethod.Head && body.Length > 0)
                        request.Content = new StringContent(body, Encoding.UTF8, contentType);
                    lastResponse?.Dispose(); lastResponse = null;
                    lastResponse = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    lastBody = await ReadBoundedBodyAsync(lastResponse.Content, maxResponseBytes, timeout.Token).ConfigureAwait(false);
                    responseComplete = true;
                    if (lastResponse.IsSuccessStatusCode) break;
                    var code = (int)lastResponse.StatusCode;
                    lastError = new HttpRequestException(
                        $"HTTP request returned {code} ({lastResponse.StatusCode}) after attempt {attempts}/{retries + 1}.",
                        null, lastResponse.StatusCode);
                    var retryable = code is 408 or 429 || code is >= 500 and < 600;
                    if (attempt == retries || !retryable)
                        break; // do not retry most 4xx
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Preserve OperationCanceledException so callers/tests treating linked timeout as cancel still work.
                    lastError = new TaskCanceledException($"HTTP request timed out after {timeoutMs} ms (attempt {attempts}/{retries + 1}).", ex);
                    if (attempt == retries) break;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                    if (attempt == retries) break;
                }
                if (attempt < retries)
                    await Task.Delay(Math.Min(retryDelayMs * (attempt + 1), 30000), cancellationToken).ConfigureAwait(false);
            }

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var statusCode = lastResponse is null ? 0 : (int)lastResponse.StatusCode;
            var success = responseComplete && lastResponse?.IsSuccessStatusCode == true;
            var responseBody = lastBody ?? string.Empty;
            var reasonPhrase = lastResponse?.ReasonPhrase;
            var status = lastResponse?.StatusCode;
            var warnings = retrySuppressed ? 1 : 0;
            var diag = NodeCompletionGuards.BuildDiagnostics(Type, NodeCompletionGuards.DiagnosticsLevel(node), warnings, attempts,
                ("url", uri.GetLeftPart(UriPartial.Path)), ("method", methodText), ("statusCode", statusCode),
                ("success", success), ("attempts", attempts), ("retries", retries), ("timeoutMs", timeoutMs),
                ("elapsedMs", elapsedMs), ("responseLength", responseBody.Length), ("requestBytes", requestBytes),
                ("requestedRetries", requestedRetries), ("retrySuppressed", retrySuppressed), ("idempotencyKeyPresent", idempotencyKey.Length > 0),
                ("requestBodySource", hasValueInput ? "value-json" : hasBodyInput ? "body-input" : "parameter"));
            if (!success)
                throw lastError ?? new HttpRequestException($"HTTP request failed after {attempts} attempt(s).", null, status);

            return new NodeExecutorResult(new Dictionary<string, VisionValue>
            {
                ["statusCode"] = VisionValue.Integer(statusCode),
                ["body"] = VisionValue.String(responseBody),
                ["success"] = VisionValue.Boolean(true),
                ["attempts"] = VisionValue.Integer(attempts),
                ["attemptCount"] = VisionValue.Integer(attempts), ["warningCount"] = VisionValue.Integer(warnings),
                ["responseLength"] = VisionValue.Integer(responseBody.Length),
                ["elapsedMs"] = VisionValue.Double(elapsedMs),
                ["diagnostics"] = VisionValue.String(diag)
            }, new Dictionary<string, object?>
            {
                ["statusCode"] = statusCode, ["reasonPhrase"] = reasonPhrase, ["responseLength"] = responseBody.Length,
                ["attempts"] = attempts, ["elapsedMs"] = Math.Round(elapsedMs, 3), ["warningCount"] = warnings, ["retrySuppressed"] = retrySuppressed, ["diagnostics"] = diag
            });
        }
        finally { lastResponse?.Dispose(); }
    }

    private static int Integer(NodeDefinition node, string name, int fallback, int min, int max)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < min || value > max)
            throw new InvalidOperationException($"HTTP {name} 必须为 {min}..{max} 内的整数。");
        return (int)value;
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
