using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

public class IsolatedApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _configuredDataRoot;
    private TempWebHostEnvironment? _dataDirectory;
    protected virtual string DataRoot => _configuredDataRoot ?? (_dataDirectory ??= new()).ContentRootPath;

    public IsolatedApiFactory() { }
    public IsolatedApiFactory(string dataRoot) => _configuredDataRoot = dataRoot;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?> { [VisionStudioDataRoot.ConfigurationKey] = DataRoot }));
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _dataDirectory?.Dispose();
    }
}

internal static class ProductionConfigTestClient
{
    public static async Task<HttpResponseMessage> PutProductionConfigAsync<T>(this HttpClient client, T config, string? revision = null)
    {
        if (revision is null)
        {
            using var current = await client.GetAsync("/api/production/config");
            current.EnsureSuccessStatusCode();
            revision = current.Headers.ETag!.ToString();
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/production/config") { Content = JsonContent.Create(config) };
        request.Headers.TryAddWithoutValidation("If-Match", revision);
        return await client.SendAsync(request);
    }
}

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
