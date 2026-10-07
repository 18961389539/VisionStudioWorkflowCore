using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed record ParameterTuningPreviewRequest(
    string DatasetId,
    string ItemId,
    WorkflowDefinition Workflow,
    string? TargetNodeId = null,
    bool FullWorkflow = false);

public sealed record ParameterTuningPreviewResult(
    string DatasetId,
    string ItemId,
    string SourceKind,
    string SourceRef,
    string ExpectedDisposition,
    string? ActualDisposition,
    string? Classification,
    bool FullWorkflow,
    NodeRunReport? SelectedNodeReport,
    OfflineReplayResult Replay);

/// <summary>
/// V0.49 transient tuning preview. Parameter drags must not flood production trace history;
/// the result remains in RunStore long enough for preview/overlay rendering, while durable
/// evidence is created only when the engineer starts a Dataset Validation run.
/// </summary>
public sealed class ParameterTuningService(
    DatasetValidationStore datasets,
    MediaLibraryService media,
    OfflineReplayService replay)
{
    public async Task<ParameterTuningPreviewResult> PreviewAsync(ParameterTuningPreviewRequest request, CancellationToken ct)
    {
        if (request.Workflow is null) throw new ApiValidationException("Candidate workflow is required.");
        var dataset = await datasets.GetDatasetAsync(request.DatasetId, ct);
        var item = dataset.Items.FirstOrDefault(x => x.ItemId.Equals(request.ItemId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ApiNotFoundException($"Dataset item '{request.ItemId}' was not found in '{request.DatasetId}'.");
        if (!item.ReplayReady) throw new ApiConflictException("The selected dataset sample is not replay-ready.");
        if (request.Workflow.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase)))
            throw new ApiConflictException("V0.49 tuning does not replay synchronized FrameSet workflows yet.");

        var topology = DatasetValidationStore.TopologyHash(request.Workflow);
        if (!string.IsNullOrWhiteSpace(dataset.Dataset.TopologyHash) &&
            !string.Equals(dataset.Dataset.TopologyHash, topology, StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("Candidate workflow topology differs from the Dataset topology. Parameter/ROI edits are allowed; node/edge changes must be validated as a separate workflow.");

        if (!request.FullWorkflow && string.IsNullOrWhiteSpace(request.TargetNodeId))
            throw new ApiValidationException("TargetNodeId is required for node preview.");
        if (!request.FullWorkflow && !request.Workflow.Nodes.Any(x => x.Id.Equals(request.TargetNodeId, StringComparison.OrdinalIgnoreCase)))
            throw new ApiValidationException($"Target node '{request.TargetNodeId}' is not present in the candidate workflow.");

        var options = request.FullWorkflow
            ? new VisionRunOptions(DebugRunMode.Full)
            : new VisionRunOptions(DebugRunMode.RunNode, request.TargetNodeId, []);

        OfflineReplayResult result;
        if (item.SourceKind == "TRACE")
        {
            result = await replay.ExecuteAsync(item.SourceRef, new OfflineReplayRequest(request.Workflow, options), "TuningPreview", ct,
                persistReplayInput: false, persistTrace: false);
        }
        else
        {
            var path = media.ResolveItemPath(item.SourceRef);
            result = await replay.ExecuteImageAsync(item.SourceRef, path, request.Workflow, "TuningPreview", options, ct,
                persistReplayInput: false, persistTrace: false);
        }

        string? actual = null;
        string? classification = null;
        if (request.FullWorkflow)
        {
            actual = result.Success ? NormalizeActual(result.QualityDisposition ?? "OK") : "ERROR";
            classification = Classify(item.ExpectedDisposition, actual);
        }

        var selected = string.IsNullOrWhiteSpace(request.TargetNodeId)
            ? null
            : result.NodeReports.LastOrDefault(x => x.NodeId.Equals(request.TargetNodeId, StringComparison.OrdinalIgnoreCase));

        return new ParameterTuningPreviewResult(
            request.DatasetId, item.ItemId, item.SourceKind, item.SourceRef, item.ExpectedDisposition,
            actual, classification, request.FullWorkflow, selected, result);
    }

    public static string NormalizeActual(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized == "OK") return "OK";
        if (normalized is "NG" or "REVIEW") return "NG";
        return "ERROR";
    }

    public static string Classify(string expected, string actual) => (expected.Trim().ToUpperInvariant(), actual) switch
    {
        (_, "ERROR") => "ERROR",
        ("OK", "OK") => "TRUE_OK",
        ("NG", "NG") => "TRUE_NG",
        ("NG", "OK") => "FALSE_OK",
        ("OK", "NG") => "FALSE_NG",
        _ => "ERROR"
    };
}
