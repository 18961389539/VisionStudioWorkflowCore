using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 诊断包（2026-11-12）：现场排障入口。断言"能下载 + 关键条目存在 + manifest 指向数据根"，
/// 不逐字段比对内容——内容是运行期快照，逐字段断言只会制造维护噪音。
/// </summary>
public sealed class DiagnosticBundleTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public DiagnosticBundleTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Bundle_IsAZipWithManifestSummaryAndReadme()
    {
        var response = await _client.GetAsync("/api/diagnostics/bundle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("visionstudio-diagnostics-", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("manifest.json", names);
        Assert.Contains("README.txt", names);
        Assert.Contains("diagnostics/summary.json", names);
        Assert.Contains("diagnostics/node-durations.json", names);
        Assert.Contains("audit/tail.json", names);

        var manifestEntry = archive.GetEntry("manifest.json")!;
        await using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("dataRoot").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("application").GetString()));
        Assert.True(manifest.RootElement.GetProperty("logging").TryGetProperty("droppedEntries", out _));
        Assert.True(manifest.RootElement.GetProperty("auditPersistence").TryGetProperty("failures", out _));
    }
}
