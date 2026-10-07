using VisionStudio.Api;
using VisionStudio.Api.Endpoints;
using VisionStudio.Api.Hosting;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Api.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVisionStudioHost(builder.Configuration, builder.Environment);

var app = builder.Build();

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
