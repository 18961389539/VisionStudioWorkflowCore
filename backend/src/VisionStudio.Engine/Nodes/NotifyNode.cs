using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 告警通知（output.notify，2026-10-27 · 缺口分析 P0-6）。
///
/// 现场缺口：产线异常只能依赖外部系统轮询 HTTP 或盯 PLC 报警字。流程自己发现"连续 3 件 NG""相机掉线"
/// 之后没有任何手段把这句话送出去，值班手机上永远缺那一条消息。
///
/// 三条通道，覆盖国内产线最常见的三种落地方式：
/// - webhook：任意 HTTP 端点，POST 一个结构化 JSON（MES / 自建告警平台）；
/// - wecom：企业微信群机器人，直接 POST msgtype=text（最省事的值班通道）；
/// - smtp：邮件，给质量/设备部门留书面记录。
///
/// 失败语义（这条最容易被写错）：
/// - 默认 `failOnError=true`：**发不出去就失败**。通知丢失比流程红一次危险得多，静默成功会让
///   "异常已上报"变成假象，等到事后追责才发现没人收到消息。
/// - 只对可恢复错误重试（超时、连接失败、5xx）；4xx 是配置错误，重试没有意义，立刻失败。
/// - 企业微信 text 通道有 2048 字节上限：超长会截断并在 outputs/diagnostics 里标记 truncated，
///   而不是把一条"告警"变成另一个异常。
/// </summary>
public sealed class NotifyNode(HttpClient? httpClient = null) : IVisionNodeExecutor
{
    public string Type => "output.notify";

    /// <summary>告警正文里全是中文，默认编码器会把它们转成 \uXXXX，值班人员在日志里根本读不出来。</summary>
    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = httpClient ?? SharedClient;

    /// <summary>企业微信 text 消息内容上限（UTF-8 字节）。</summary>
    private const int WeComTextLimitBytes = 2048;

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var channel = node.GetString("channel", "webhook").Trim().ToLowerInvariant();
        if (channel is not ("webhook" or "wecom" or "smtp"))
            throw new InvalidOperationException("output.notify channel must be webhook, wecom or smtp.");

        var message = (context.TryGetValue("message", out var messageInput)
            ? messageInput.Require<string>("message")
            : node.GetString("message", string.Empty)).Trim();
        var subject = node.GetString("subject", "VisionStudio alarm").Trim();
        if (message.Length == 0 && !context.TryGetValue("value", out _))
            throw new InvalidOperationException("output.notify needs a message (connected or configured) or a value input.");

        var timeoutMs = ProtocolFraming.RequireRange(node.GetInt("timeoutMs", 5000), 200, 120_000, "output.notify timeoutMs");
        var retries = ProtocolFraming.RequireRange(node.GetInt("retries", 1), 0, 5, "output.notify retries");
        var retryDelayMs = ProtocolFraming.RequireRange(node.GetInt("retryDelayMs", 500), 0, 10_000, "output.notify retryDelayMs");
        var failOnError = node.GetBool("failOnError", true);
        var maxPayloadBytes = ProtocolFraming.RequireRange(node.GetInt("maxPayloadBytes", 32768), 256, 1_048_576, "output.notify maxPayloadBytes");

        var timer = Stopwatch.StartNew();
        var redactor = new NotificationRedactor();
        redactor.AddSecret(node.GetString("smtpPassword", string.Empty));
        var attempts = 0;
        var truncated = false;
        var statusCode = 0;
        string? responseBody = null;
        string? failure = null;
        string? failureKind = null;

        try
        {
            if (channel == "smtp")
            {
                for (var attempt = 1; attempt <= retries + 1; attempt++)
                {
                    attempts = attempt;
                    try
                    {
                        await SendMailAsync(node, subject, message, maxPayloadBytes, cancellationToken).ConfigureAwait(false);
                        failure = null; failureKind = null;
                        break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failure = $"timeout after {timeoutMs} ms"; failureKind = "timeout";
                    }
                    catch (SmtpException ex)
                    {
                        failureKind = ClassifySmtpFailure(ex);
                        failure = $"SMTP {failureKind}: {ex.Message}";
                        if (!IsRecoverableFailureKind(failureKind)) break;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    {
                        failure = ex.Message; failureKind = "network";
                    }
                    if (attempt <= retries && retryDelayMs > 0)
                        await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var url = (context.TryGetValue("url", out var urlInput)
                    ? urlInput.Require<string>("url")
                    : node.GetString("url", string.Empty)).Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("output.notify url must be an absolute http or https URL.");
                redactor.AddUri(uri);

                var (payload, wasTruncated) = BuildPayload(channel, context, node, subject, message);
                truncated = wasTruncated;
                var bytes = Encoding.UTF8.GetByteCount(payload);
                if (bytes > maxPayloadBytes)
                    throw new InvalidOperationException(
                        $"output.notify payload is {bytes} bytes which exceeds maxPayloadBytes={maxPayloadBytes}; shorten the message or raise the limit.");

                for (var attempt = 1; attempt <= retries + 1; attempt++)
                {
                    attempts = attempt;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(timeoutMs);
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };
                    try
                    {
                        using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                        statusCode = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        responseBody = body.Length > 512 ? body[..512] : body;
                        if (response.IsSuccessStatusCode) { failure = null; break; }

                        // Never echo an arbitrary remote error body: webhook endpoints often
                        // reflect the token, credential-bearing value, or request headers.
                        failure = $"HTTP {statusCode}";
                        failureKind = statusCode is 401 or 403 ? "authentication"
                            : statusCode >= 400 && statusCode < 500 ? "client"
                            : "server";
                        // 4xx 是配置问题（URL/令牌/字段），重试只会把一次错配置刷成 N 次告警。
                        if (statusCode is >= 400 and < 500) break;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failure = $"timeout after {timeoutMs} ms";
                        failureKind = "timeout";
                    }
                    catch (HttpRequestException ex)
                    {
                        failure = ex.Message;
                        failureKind = "network";
                    }

                    if (attempt <= retries && retryDelayMs > 0)
                        await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.Message;
            failureKind = ex is SmtpException smtpException ? ClassifySmtpFailure(smtpException) : "configuration";
        }

        timer.Stop();
        responseBody = redactor.Redact(responseBody);
        failure = redactor.Redact(failure);
        var delivered = failure is null;
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("channel", channel), ("delivered", delivered), ("attempts", attempts),
            ("statusCode", statusCode), ("truncated", truncated), ("reason", failure),
            ("failureKind", failureKind), ("retryable", IsRecoverableFailureKind(failureKind)),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["delivered"] = VisionValue.Boolean(delivered),
            ["attempts"] = VisionValue.Integer(attempts),
            ["truncated"] = VisionValue.Boolean(truncated),
            ["failureKind"] = VisionValue.String(failureKind ?? "none"),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (statusCode > 0) outputs["statusCode"] = VisionValue.Integer(statusCode);
        if (responseBody is { Length: > 0 }) outputs["responseBody"] = VisionValue.String(responseBody);

        if (!delivered && failOnError)
            throw new InvalidOperationException($"output.notify failed on channel '{channel}': {failure}");

        return new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["channel"] = channel, ["delivered"] = delivered, ["attempts"] = attempts,
                ["statusCode"] = statusCode, ["truncated"] = truncated, ["reason"] = failure,
                ["failureKind"] = failureKind, ["retryable"] = IsRecoverableFailureKind(failureKind),
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            });
    }

    private static (string Payload, bool Truncated) BuildPayload(
        string channel, NodeExecutionContext context, NodeDefinition node, string subject, string message)
    {
        if (channel == "wecom")
        {
            var (content, truncated) = TruncateUtf8(ComposePlainText(subject, message), WeComTextLimitBytes);
            var payload = JsonSerializer.Serialize(new { msgtype = "text", text = new { content } }, JsonText);
            return (payload, truncated);
        }

        object? value = null;
        var valueType = "none";
        if (context.TryGetValue("value", out var valueInput))
        {
            valueType = valueInput.Type.ToString();
            value = valueInput.Value switch
            {
                IVisionImage image => new { image.Width, image.Height },
                IVisionFrameSet frames => new
                {
                    frames.GroupId, frames.TimestampBasis, frames.TriggerSkewUs, frames.WithinTolerance,
                    cameras = frames.Frames.Keys.Order().ToArray()
                },
                _ => valueInput.Value
            };
        }

        var text = ComposePlainText(subject, message);
        var body = JsonSerializer.Serialize(new
        {
            subject,
            message = text,
            valueType,
            value,
            source = node.GetString("source", string.Empty) is { Length: > 0 } tag ? tag : null,
            timestampUtc = DateTimeOffset.UtcNow
        }, JsonText);
        return (body, false);
    }

    private static string ComposePlainText(string subject, string message)
        => string.IsNullOrWhiteSpace(message) ? subject : $"{subject}\n{message}";

    /// <summary>按 UTF-8 字节截断且不切断字符：中文告警在 2048 字节里只够 600 多字。</summary>
    private static (string Text, bool Truncated) TruncateUtf8(string text, int limitBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= limitBytes) return (text, false);
        var builder = new StringBuilder(text.Length);
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = Encoding.UTF8.GetByteCount(rune.ToString());
            if (used + size > limitBytes - 3) break; // 预留 "…" 的空间
            builder.Append(rune.ToString());
            used += size;
        }
        builder.Append('…');
        return (builder.ToString(), true);
    }

    /// <summary>
    /// Redacts both configured secrets and credential-shaped content reflected by a remote
    /// endpoint. The outgoing payload is deliberately not changed; only observable failure
    /// and response text passes through this class.
    /// </summary>
    private sealed class NotificationRedactor
    {
        private static readonly Regex CredentialAssignment = new(
            "(?<prefix>[\"']?(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|api[_-]?key|secret|client[_-]?secret|authorization|proxy-authorization|credential|credentials|signature|sig|key)[\"']?\\s*[:=]\\s*)(?<value>\"(?:\\\\.|[^\"\\\\])*\"|'[^']*'|[^\\s&,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AuthorizationValue = new(
            "\\b(?:Bearer|Basic)\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex UriUserInfo = new(
            "(?<scheme>https?://)[^/\\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

        public void AddSecret(string? secret)
        {
            if (string.IsNullOrEmpty(secret)) return;
            _secrets.Add(secret);
            _secrets.Add(Uri.UnescapeDataString(secret));
            // A credential containing quotes or backslashes may be reflected JSON-escaped.
            _secrets.Add(JsonSerializer.Serialize(secret)[1..^1]);
        }

        public void AddUri(Uri uri)
        {
            AddSecret(uri.ToString());
            AddSecret(uri.PathAndQuery);
            foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment.Length >= 8) AddSecret(Uri.UnescapeDataString(segment));
            if (uri.UserInfo.Length > 0)
            {
                AddSecret(uri.UserInfo);
                foreach (var part in uri.UserInfo.Split(':', 2)) AddSecret(Uri.UnescapeDataString(part));
            }
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
                if (value.Length > 0) AddSecret(Uri.UnescapeDataString(value));
            }
            if (uri.Fragment.Length > 0) AddSecret(Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
        }

        public string? Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var secret in _secrets.OrderByDescending(value => value.Length, Comparer<int>.Default))
                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            text = AuthorizationValue.Replace(text, "[REDACTED]");
            text = UriUserInfo.Replace(text, "${scheme}[REDACTED]@");
            return CredentialAssignment.Replace(text, match =>
            {
                var value = match.Groups["value"].Value;
                var quoted = value.Length >= 2 && value[0] is '\"' or '\'' && value[^1] == value[0];
                return match.Groups["prefix"].Value + (quoted ? value[..1] + "[REDACTED]" + value[^1..] : "[REDACTED]");
            });
        }
    }

    private static string ClassifySmtpFailure(SmtpException ex)
        => (int)ex.StatusCode is 530 or 534 or 535 ? "authentication"
            : (int)ex.StatusCode is >= 400 and < 500 ? "client"
            : (int)ex.StatusCode is >= 500 and < 600 ? "server"
            : ex.StatusCode is SmtpStatusCode.MailboxBusy or SmtpStatusCode.InsufficientStorage or SmtpStatusCode.SystemStatus
                ? "server" : "smtp";

    private static bool IsRecoverableFailureKind(string? kind)
        => kind is "timeout" or "network" or "server";

    private static async Task SendMailAsync(
        NodeDefinition node, string subject, string message, int maxPayloadBytes, CancellationToken cancellationToken)
    {
        var host = node.GetString("smtpHost", string.Empty).Trim();
        if (host.Length == 0) throw new InvalidOperationException("output.notify smtpHost is required for the smtp channel.");
        var port = ProtocolFraming.RequireRange(node.GetInt("smtpPort", 587), 1, 65535, "output.notify smtpPort");
        var from = node.GetString("smtpFrom", string.Empty).Trim();
        if (from.Length == 0) throw new InvalidOperationException("output.notify smtpFrom is required for the smtp channel.");
        var recipients = (node.GetString("smtpTo", string.Empty) ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0) throw new InvalidOperationException("output.notify smtpTo is required for the smtp channel.");

        var bodyBytes = Encoding.UTF8.GetByteCount(message);
        if (bodyBytes > maxPayloadBytes)
            throw new InvalidOperationException(
                $"output.notify mail body is {bodyBytes} bytes which exceeds maxPayloadBytes={maxPayloadBytes}.");

        using var mail = new MailMessage { From = new MailAddress(from), Subject = subject, Body = message };
        foreach (var recipient in recipients) mail.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = node.GetBool("smtpSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        var user = node.GetString("smtpUser", string.Empty).Trim();
        if (user.Length > 0)
            client.Credentials = new NetworkCredential(user, node.GetString("smtpPassword", string.Empty));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolFraming.RequireRange(node.GetInt("timeoutMs", 5000), 200, 120_000, "output.notify timeoutMs"));
        await client.SendMailAsync(mail, timeout.Token).ConfigureAwait(false);
    }
}
