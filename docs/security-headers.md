# 响应安全头

`SecurityHeadersMiddleware` 为**所有**响应（API、静态文件、SPA 回退、problem+json 错误）附加：

| 响应头 | 默认值 | 作用 |
|---|---|---|
| `Content-Security-Policy` | 见下 | 限定脚本/样式/图像/连接来源，纵深防御 XSS |
| `X-Content-Type-Options` | `nosniff` | 阻止浏览器把 JSON 猜成脚本 |
| `X-Frame-Options` | `DENY` | 旧浏览器的防嵌入（现代浏览器由 `frame-ancestors 'none'` 覆盖） |
| `Referrer-Policy` | `no-referrer` | 跨站请求不携带 URL（追溯页 URL 含 runId 等标识） |
| `Strict-Transport-Security` | `max-age=31536000` | **仅 HTTPS 响应发送**；HTTP 上不出现 |

默认 CSP：

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:;
worker-src 'self' blob:; object-src 'none'; base-uri 'self';
form-action 'self'; frame-ancestors 'none'
```

## 为什么不是"越严越好"

每一项放宽都对应前端真实用法，收紧前必须先移除用法，否则现场表现为"页面空白 + 控制台 CSP 报错"：

- `style-src 'unsafe-inline'`：antd 的 CSS-in-JS 会注入 `<style>` 标签；
- `img-src ... blob:`：图像预览与下载使用 `URL.createObjectURL`；
- `img-src ... data:`：内联小图形/图标；
- `connect-src ... ws: wss:`：SignalR（`/hubs/diagnostics`、`/hubs/monitor`）走 WebSocket。
  部分浏览器不把 `'self'` 视作 ws 同源，显式列出 `ws:`/`wss:`；
- `script-src 'self'`：生产构建的 `index.html` 只引外部 module 脚本，没有内联脚本/eval。

## 实现要点

- 中间件在 `UseForwardedHeaders()` 之后、静态文件/异常处理器之前接入（`Program.cs`）；
- 响应头通过 `Response.OnStarting` 写入：异常处理器会重置响应头，直接写会让 problem+json
  错误响应丢头；`OnStarting` 在最终响应开始写出前补上，正常与错误路径同口径；
- HSTS 依据 `Request.IsHttps` 判定——在 TLS 终止代理后运行时，由转发头中间件改写后的
  scheme 决定，避免"代理后 HTTPS、宿主看 HTTP"时漏发或误发；
- 默认**不带** `includeSubDomains`：本应用通常只是站点的一个子域，替整个域做承诺需要现场确认。
  需要时在站点配置里改为 `"max-age=31536000; includeSubDomains"`。

## 配置

`appsettings.json` → `site-config/appsettings.site.json` → 环境变量 → 命令行（优先级递增）：

```json
{
  "SecurityHeaders": {
    "Enabled": true,
    "ContentSecurityPolicy": "...",
    "ContentTypeOptions": "nosniff",
    "ReferrerPolicy": "no-referrer",
    "FrameOptions": "DENY",
    "Hsts": "max-age=31536000"
  }
}
```

`Enabled=false` 只用于隔离排障（例如确认某个第三方集成被 CSP 拦截），不要作为生产常态。

## 回归

`backend/tests/VisionStudio.Api.Tests/SecurityHeadersTests.cs`：

- `ApiResponse_CarriesSecurityHeaders`：正常 API 响应含全部头；HTTP 不含 HSTS；
  CSP 保留 antd（`style-src 'unsafe-inline'`）、blob 图像与 SignalR（`connect-src ws: wss:`）所需来源。
- `ProblemDetailsErrorResponse_AlsoCarriesSecurityHeaders`：404 problem+json 路径同样带头
  （验证异常处理器重置响应头后仍生效）。
- `HttpsRequest_CarriesHsts`：HTTPS 请求带 HSTS。
- `DisabledConfiguration_EmitsNoSecurityHeaders`：开关关闭时不输出。

`scripts/static-audit.py` 同时断言中间件接入顺序（转发头之后、静态文件/异常处理器之前）、
配置项、测试与本文档存在。