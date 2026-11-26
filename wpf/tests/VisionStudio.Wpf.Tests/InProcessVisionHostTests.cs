using System.IO;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using Xunit;

namespace VisionStudio.Wpf.Tests;

public sealed class InProcessVisionHostTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Host_BootsInProcess_RunsSampleWorkflow_AndPersistsTrace()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        var contentRoot = DesktopWebHostEnvironment.ResolveDefaultContentRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot, contentRoot);

            Assert.NotEmpty(host.NodeRegistry.Catalog);
            Assert.NotEmpty(host.Cameras.List());

            var samplePath = Path.Combine(AppContext.BaseDirectory, "samples", "linear-workflow.json");
            Assert.True(File.Exists(samplePath), $"Sample file not found: {samplePath}");

            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(await File.ReadAllTextAsync(samplePath), JsonOptions);
            Assert.NotNull(workflow);

            var compiled = await host.CompileAndValidateAsync(workflow!);
            Assert.NotEmpty(compiled.OrderedNodeIds);

            var result = await host.RunWorkflowAsync(workflow!);
            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(result.NodeReports);
            Assert.True(result.PreviewAvailable);

            var traces = await host.Traces.ListAsync(10, null, null, CancellationToken.None);
            Assert.Contains(traces, t => t.RunId == result.RunId);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Host_GrabsCameraMatInMemory_AndConvertsToFrozenBitmapSource()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        var contentRoot = DesktopWebHostEnvironment.ResolveDefaultContentRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot, contentRoot);
            var firstCamera = host.Cameras.List().First();

            var (mat, sequence, _) = await host.GrabCameraMatAsync(firstCamera.Id);
            using (mat)
            {
                Assert.False(mat.Empty());
                Assert.True(sequence > 0);

                var bitmap = VisionImageInterop.ToBitmapSource(mat);
                Assert.True(bitmap.IsFrozen);
                Assert.Equal(mat.Cols, bitmap.PixelWidth);
                Assert.Equal(mat.Rows, bitmap.PixelHeight);
            }
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }
}
