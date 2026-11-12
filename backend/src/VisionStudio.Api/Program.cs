using VisionStudio.Api;
using VisionStudio.Api.Endpoints;
using VisionStudio.Api.Hosting;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Api.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// F06：站点配置源（数据根 site-config/appsettings.site.json）——现场策略覆盖内容根 appsettings、
// 被环境变量/命令行覆盖；该文件随系统备份往返，恢复数据根即恢复"实际生效的配置"。
VisionStudioSiteConfig.AddSource(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddVisionStudioHost(builder.Configuration, builder.Environment);

var app = builder.Build();

// Startup banner: make the actual content root, data directory, environment and version visible,
// so a wrong working directory (which would silently create another database) is immediately obvious.
app.Logger.LogInformation(
    "VisionStudio starting: env={Environment} contentRoot={ContentRoot} dataDir={DataDir} version={Version}",
    app.Environment.EnvironmentName,
    app.Environment.ContentRootPath,
    VisionStudioDataRoot.Resolve(app.Environment.ContentRootPath),
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");

// 反向代理策略进启动横幅：部署在代理后面却没开 X-Forwarded-*，表现是"全厂共享登录配额"
// 和"审计 IP 全是代理地址"，从日志必须一眼看出来。
app.Logger.LogInformation("Reverse proxy forwarded headers: {ForwardedHeaders}",
    ReverseProxyRegistration.Describe(app.Configuration));

// 必须是管线里第一个中间件：限流分区、Secure Cookie 判定、审计来源 IP 都依赖它改写后的
// RemoteIpAddress / Scheme。放在后面等于没配。
app.UseForwardedHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseMiddleware<AuditMiddleware>();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseMiddleware<StorageMaintenanceMiddleware>();
app.UseAuthentication();
app.UseMiddleware<RuntimeOnlyMiddleware>();
app.UseAuthorization();

// OpenAPI 文档是完整的攻击面清单（全部路由、DTO 字段、枚举取值），不该匿名挂在生产端口上。
// Development 保留匿名：scripts/export-openapi.ps1 与前端契约对账都依赖它（脚本以
// ASPNETCORE_ENVIRONMENT=Development 启动），否则快照导出必须先造一个管理员会话。
var openApi = app.MapOpenApi();
if (app.Environment.IsDevelopment()) openApi.AllowAnonymous();
else openApi.RequireAuthorization(SecurityPolicies.Administrator);

app.MapVisionStudioApi();
app.MapHub<DiagnosticsHub>("/hubs/diagnostics").RequireAuthorization();
// 设备监视推送（2026-11-12）：替代各面板的高频轮询，内容变化时才广播。
app.MapHub<MonitorHub>("/hubs/monitor").RequireAuthorization();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
