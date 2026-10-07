using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace VisionStudio.Api.Tests;

internal sealed class TempWebHostEnvironment : IWebHostEnvironment, IDisposable
{
    public TempWebHostEnvironment()
    {
        ContentRootPath = Path.Combine(Path.GetTempPath(), "visionstudio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRootPath);
    }

    public string ApplicationName { get; set; } = "VisionStudio.Api.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public void Dispose()
    {
        try { Directory.Delete(ContentRootPath, recursive: true); }
        catch { /* temp cleanup must not hide test result */ }
    }
}
