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

app.MapOpenApi().AllowAnonymous();
app.MapVisionStudioApi();
app.MapHub<DiagnosticsHub>("/hubs/diagnostics").RequireAuthorization();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
