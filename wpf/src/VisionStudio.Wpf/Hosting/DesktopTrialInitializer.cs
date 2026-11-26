using System.IO;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Wpf.ViewModels;

namespace VisionStudio.Wpf.Hosting;

public static class DesktopTrialInitializer
{
    public const string JobId = "wpf-trial-synthetic";

    public static async Task<JobDescriptor> InitializeAsync(InProcessVisionHost host, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(host.SamplesDirectory, "linear-workflow.json"), cancellationToken);
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("试用流程文件为空。");
        WorkflowDocumentMapper.ValidateDefinition(workflow);
        var expanded = await host.ModuleExpander.ExpandAsync(workflow, cancellationToken);
        host.Compiler.Compile(expanded.Workflow);
        if (NodeMaturityGate.Scan(expanded.Workflow).Count > 0
            || DeviceSideEffectClassifier.HasDeviceSideEffects(expanded.Workflow, host.NodeRegistry))
        {
            throw new InvalidOperationException("预置试用流程必须仅包含 Stable 节点，且不能包含设备副作用。");
        }

        var job = await host.Jobs.GetAsync(JobId, cancellationToken);
        if (job is not null)
        {
            var original = await host.Jobs.GetVersionAsync(JobId, 1, cancellationToken);
            if (job.LatestVersion != 1 || original?.WorkflowHash != WorkflowFingerprint.Compute(workflow))
                throw new InvalidOperationException("示例工单 ID 已有不同内容；为保护现有数据，不能覆盖。");
            if (job.PublishedVersion == 1) return job;
        }

        await RecordActionAsync(host, "wpf.trial.initialize.intent", JobId, 202, cancellationToken);
        job ??= await host.Jobs.CreateAsync(new CreateJobRequest(
            JobId, "内部试用 · 合成图像检测", "仅用于无硬件的内部试用", workflow), cancellationToken);
        var manifest = await host.Dependencies.CaptureAsync(workflow, cancellationToken);
        var published = await host.Jobs.PublishAsync(
            JobId, 1, "Publish", manifest, cancellationToken, job.UpdatedAt);
        await RecordActionAsync(host, "wpf.trial.initialize", JobId, 200, cancellationToken);
        return published;
    }

    public static Task RecordActionAsync(InProcessVisionHost host, string action, string target, int statusCode, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, System.Environment.UserName), new Claim(ClaimTypes.Role, "LocalDesktopTrial")],
                "LocalDesktop"))
        };
        context.Request.Method = "DESKTOP";
        context.Request.Path = "/wpf/trial";
        AuditContext.SetTarget(context, target);
        return host.Services.GetRequiredService<AuditEventStore>().RecordAsync(
            context, new AuditActionMetadata(action, "desktop"), statusCode, cancellationToken);
    }
}
