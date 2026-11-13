using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api;

public sealed record ProductionPtpGuardIssue(
    string Code,
    string GroupId,
    string Message);

public sealed record ProductionPtpGuardGroupStatus(
    string GroupId,
    string GroupName,
    bool RequiredByScheduledCapture,
    bool RequiredByGroupPolicy,
    bool LiveReady,
    int WindowRuns,
    int EvidenceRuns,
    double? PtpReadyRate,
    double? P95MaxAbsOffsetNs,
    int MasterClockChanges,
    int InconsistentMasterClockRuns,
    string? CurrentMasterClockId,
    bool Healthy,
    string? Reason);

public sealed record ProductionPtpGuardSnapshot(
    bool Enabled,
    bool Healthy,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ProductionPtpGuardGroupStatus> Groups,
    IReadOnlyList<ProductionPtpGuardIssue> Issues,
    string? Summary = null)
{
    public static ProductionPtpGuardSnapshot Disabled { get; } = new(false, true, DateTimeOffset.MinValue, [], [], "PTP drift guard disabled.");
}

/// <summary>
/// Production-only policy layer over the canonical camera synchronization run history.
/// It does not create a second timing store: every decision is derived from camera_sync_runs.ptp_json
/// plus the current live group status. Only sync groups that explicitly require PTP, or are referenced
/// by a scheduled camera.syncCapture node, are guarded.
/// </summary>
public sealed class ProductionPtpDriftGuardService(CameraSynchronizationService synchronization)
{
    public async Task<ProductionPtpGuardSnapshot> EvaluateAsync(
        WorkflowDefinition workflow,
        ProductionRuntimeConfig config,
        CancellationToken ct = default)
    {
        if (!config.PtpDriftGuardEnabled)
            return ProductionPtpGuardSnapshot.Disabled with { EvaluatedAt = DateTimeOffset.UtcNow };

        var requirements = ResolveRequirements(workflow);
        if (requirements.Count == 0)
            return new ProductionPtpGuardSnapshot(true, true, DateTimeOffset.UtcNow, [], [], "Workflow has no PTP-required synchronized capture nodes.");

        var groups = new List<ProductionPtpGuardGroupStatus>();
        var issues = new List<ProductionPtpGuardIssue>();
        foreach (var requirement in requirements.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            CameraSynchronizationGroup group;
            try
            {
                group = await synchronization.GetAsync(requirement.Key, ct);
            }
            catch (Exception ex)
            {
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-NOT-READY", requirement.Key, $"Synchronization group '{requirement.Key}' cannot be resolved: {ex.Message}"));
                groups.Add(new ProductionPtpGuardGroupStatus(requirement.Key, requirement.Key, requirement.Value.Scheduled, true, false, 0, 0, null, null, 0, 0, null, false, ex.Message));
                continue;
            }

            var requiredByPolicy = group.RequirePtpLocked;
            if (!requiredByPolicy && !requirement.Value.Scheduled)
                continue;

            CameraSynchronizationGroupStatus live;
            try
            {
                live = await synchronization.GetStatusAsync(group.Id, ct);
            }
            catch (Exception ex)
            {
                var message = $"PTP live-status probe failed for sync group '{group.Id}': {ex.Message}";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-NOT-READY", group.Id, message));
                groups.Add(new ProductionPtpGuardGroupStatus(group.Id, group.Name, requirement.Value.Scheduled, requiredByPolicy, false, 0, 0, null, null, 0, 0, null, false, message));
                continue;
            }

            var masterIds = live.Members
                .Select(x => x.TimeSync.MasterClockId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var liveMasterConsistent = masterIds.Length <= 1;
            var liveReady = live.PtpReady && liveMasterConsistent;

            var history = await synchronization.ListRunsAsync(group.Id, config.PtpGuardWindowRuns, ct);
            var diagnostics = CameraSynchronizationService.ComputePtpDiagnostics(group, history, config.PtpGuardWindowRuns);
            var evidence = diagnostics.RunsWithPtpEvidence;
            double? readyRate = evidence == 0 ? null : diagnostics.PtpReadyRuns / (double)evidence;
            var masterChanges = diagnostics.Cameras.Sum(x => x.MasterClockChanges);
            var enoughHistory = evidence >= config.PtpGuardMinimumEvidenceRuns;

            string? reason = null;
            if (!live.PtpReady)
            {
                reason = live.Error ?? $"Current PTP state is not ready for group '{group.Id}'.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-NOT-READY", group.Id, reason));
            }
            else if (!liveMasterConsistent)
            {
                reason = $"Current cameras in sync group '{group.Id}' report different PTP master clocks: {string.Join(", ", masterIds)}.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-MASTER-CLOCK", group.Id, reason));
            }
            else if (enoughHistory && readyRate is not null && readyRate.Value < config.PtpGuardMinimumReadyRate)
            {
                reason = $"PTP ready rate {readyRate.Value:P1} in the last {evidence} evidence runs is below production threshold {config.PtpGuardMinimumReadyRate:P1}.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-DRIFT", group.Id, reason));
            }
            else if (enoughHistory && diagnostics.P95MaxAbsOffsetNs is not null && diagnostics.P95MaxAbsOffsetNs.Value > group.MaxPtpOffsetNs)
            {
                reason = $"PTP p95 max |offset| {diagnostics.P95MaxAbsOffsetNs.Value:F0} ns exceeds sync-group limit {group.MaxPtpOffsetNs} ns over the last {evidence} evidence runs.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-DRIFT", group.Id, reason));
            }
            else if (enoughHistory && diagnostics.InconsistentMasterClockRuns > 0)
            {
                reason = $"{diagnostics.InconsistentMasterClockRuns} run(s) in the active PTP guard window observed inconsistent master-clock identities.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-MASTER-CLOCK", group.Id, reason));
            }
            else if (enoughHistory && config.PtpGuardFaultOnMasterClockChange && masterChanges > 0)
            {
                reason = $"PTP master clock changed {masterChanges} time(s) inside the active guard window.";
                issues.Add(new ProductionPtpGuardIssue("PROD-PTP-MASTER-CLOCK", group.Id, reason));
            }

            groups.Add(new ProductionPtpGuardGroupStatus(
                group.Id,
                group.Name,
                requirement.Value.Scheduled,
                requiredByPolicy,
                liveReady,
                history.Count,
                evidence,
                readyRate,
                diagnostics.P95MaxAbsOffsetNs,
                masterChanges,
                diagnostics.InconsistentMasterClockRuns,
                masterIds.Length == 1 ? masterIds[0] : null,
                reason is null,
                reason ?? (enoughHistory ? "PTP live state and rolling history satisfy the production guard." : $"PTP live state is ready; only {evidence}/{config.PtpGuardMinimumEvidenceRuns} historical evidence runs are available, so drift history is not yet enforced.")));
        }

        var healthy = issues.Count == 0;
        return new ProductionPtpGuardSnapshot(
            true,
            healthy,
            DateTimeOffset.UtcNow,
            groups,
            issues,
            healthy ? "PTP production guard healthy." : string.Join(" ", issues.Take(3).Select(x => x.Message)));
    }

    public static IReadOnlyDictionary<string, (bool Scheduled, string NodeId)> ResolveRequirements(WorkflowDefinition workflow)
    {
        var output = new Dictionary<string, (bool Scheduled, string NodeId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes.Where(x => string.Equals(x.Type, "camera.syncCapture", StringComparison.OrdinalIgnoreCase)))
        {
            if (node.Parameters is null || !node.Parameters.TryGetValue("groupId", out var groupElement)) continue;
            var groupId = ReadString(groupElement)?.Trim();
            if (string.IsNullOrWhiteSpace(groupId)) continue;
            var scheduled = node.Parameters.TryGetValue("scheduled", out var scheduledElement) && ReadBool(scheduledElement);
            if (output.TryGetValue(groupId, out var existing))
                output[groupId] = (existing.Scheduled || scheduled, existing.NodeId);
            else
                output[groupId] = (scheduled, node.Id);
        }
        return output;
    }

    private static string? ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();

    private static bool ReadBool(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
            _ => false
        };
}
