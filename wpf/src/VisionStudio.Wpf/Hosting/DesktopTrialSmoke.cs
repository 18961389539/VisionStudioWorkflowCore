using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.ViewModels;

namespace VisionStudio.Wpf.Hosting;

internal static class DesktopTrialSmoke
{
    public static async Task RunAsync(InProcessVisionHost host, string reportPath)
    {
        var job = await DesktopTrialInitializer.InitializeAsync(host);
        var snapshot = await host.Jobs.GetVersionAsync(job.Id, 1, CancellationToken.None)
            ?? throw new InvalidOperationException("缺少试用工单快照。");
        var workflow = snapshot.Workflow with { Id = "wpf-publish-smoke" };
        var existing = await host.WorkflowStore.LoadWithRevisionAsync(workflow.Id, CancellationToken.None);
        if (existing is { } stored && WorkflowFingerprint.Compute(stored.Workflow) != WorkflowFingerprint.Compute(workflow))
            throw new InvalidOperationException("Smoke 数据根含有用户修改，不能覆盖。");
        var saved = await host.WorkflowStore.SaveAsync(workflow.Id, workflow, CancellationToken.None, existing?.Revision ?? string.Empty);
        var result = await host.RunWorkflowAsync(workflow);
        if (!result.Success || !result.PreviewAvailable)
            throw new InvalidOperationException($"发布态流程失败：{result.Error}");
        var trace = await host.Traces.GetAsync(result.RunId, CancellationToken.None)
            ?? throw new InvalidOperationException("发布态运行未持久化追溯。");
        var (mat, sequence, _) = await host.GrabCameraMatAsync("file-demo");
        using (mat)
        {
            var image = VisionImageInterop.ToBitmapSource(mat);
            if (!image.IsFrozen || image.PixelWidth == 0)
                throw new InvalidOperationException("发布态回放帧转换失败。");
        }
        var diagnosticPath = Path.ChangeExtension(reportPath, ".diagnostics.zip");
        await new TraceDiagnosticsViewModel(host).ExportBundleAsync(diagnosticPath);
        await DesktopFileWriter.WriteAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            success = true,
            version = typeof(DesktopTrialSmoke).Assembly.GetName().Version?.ToString(),
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
            contentRoot = host.Environment.ContentRootPath,
            dataRoot = host.DataRoot,
            publishedVersion = job.PublishedVersion,
            workflowRevision = saved.Revision,
            runId = trace.RunId,
            previewAvailable = result.PreviewAvailable,
            cameraId = "file-demo",
            cameraSequence = sequence,
            diagnosticPath
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
