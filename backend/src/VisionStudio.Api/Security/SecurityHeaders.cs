using Microsoft.Extensions.Options;

namespace VisionStudio.Api.Security;

/// <summary>
/// 响应安全头（P1）。CSP 的取值不是"越严越好"——必须与前端实际加载方式一致：
/// · antd 用 CSS-in-JS 注入 &lt;style&gt;，style-src 需要 'unsafe-inline'；
/// · 图像预览/下载使用 blob: URL，img-src 需要 blob:（data: 供内联图形）；
/// · SignalR 走同源 WebSocket，connect-src 需要 ws:/wss:（部分浏览器不把 'self' 视作 ws 同源）。
/// 收紧任何一项前先确认上述用法已移除，否则会得到"界面空白、控制台报 CSP"的现场故障。
/// </summary>
public sealed class SecurityHeadersOptions
{
    public bool Enabled { get; set; } = true;
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    public string ContentTypeOptions { get; set; } = "nosniff";
    public string ReferrerPolicy { get; set; } = "no-referrer";
    /// <summary>frame-ancestors 覆盖现代浏览器；X-Frame-Options 只为旧浏览器保留同语义。</summary>
    public string FrameOptions { get; set; } = "DENY";
    /// <summary>仅 HTTPS 响应发送。默认不带 includeSubDomains（子域不一定是本应用）。</summary>
    public string? Hsts { get; set; } = "max-age=31536000";
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    private readonly SecurityHeadersOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await next(context);
            return;
        }

        // 在异常处理器之前注册：错误响应会重置响应头，直接写会丢；OnStarting 在最终响应
        // 开始写出时补上，普通响应与 problem+json 都覆盖。
        context.Response.OnStarting(() =>
        {
            Apply(context);
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        finally
        {
            // 中间件清空响应头（未开始写出的场景）时兜底一次；已开始写出则跳过。
            if (!context.Response.HasStarted) Apply(context);
        }
    }

    private void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = _options.ContentTypeOptions;
        headers.XFrameOptions = _options.FrameOptions;
        headers["Referrer-Policy"] = _options.ReferrerPolicy;
        headers.ContentSecurityPolicy = _options.ContentSecurityPolicy;
        if (!string.IsNullOrWhiteSpace(_options.Hsts) && context.Request.IsHttps)
            headers.StrictTransportSecurity = _options.Hsts;
    }
}