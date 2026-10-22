using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record InvestigationCaseSummary(
    string Id,
    string SignatureId,
    string Category,
    string Title,
    string Status,
    string Severity,
    string WorkflowId,
    string? WorkflowHash,
    string Source,
    string? JobId,
    int? JobVersion,
    string? PrimaryNodeId,
    string? PrimaryNodeType,
    string Fingerprint,
    string RepresentativeRunId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int OccurrenceCount,
    int ReopenCount,
    string? Owner,
    string? RootCause,
    string? ResolutionNote,
    string? VerificationNote,
    string? VerificationRunId,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InvestigationCaseRun(
    string RunId,
    DateTimeOffset StartedAt,
    string Disposition,
    double TotalDurationMs,
    double? NodeDurationMs);

public sealed record InvestigationCaseDetail(
    InvestigationCaseSummary Case,
    IReadOnlyList<InvestigationCaseRun> EvidenceRuns,
    IReadOnlyList<InvestigationVerificationEvidence> VerificationEvidence);

public sealed record CreateInvestigationVerificationRequest(
    string DatasetId,
    string BaselineValidationRunId,
    string CandidateValidationRunId,
    string? Note = null);

public sealed record InvestigationVerificationEvidence(
    string Id,
    string CaseId,
    string DatasetId,
    string DatasetName,
    string BaselineValidationRunId,
    string CandidateValidationRunId,
    string BaselineWorkflowHash,
    string CandidateWorkflowHash,
    string GateStatus,
    int BaselineSignatureHits,
    int CandidateSignatureHits,
    int BaselineInspectableResults,
    int CandidateInspectableResults,
    ValidationRunSummary BaselineSummary,
    ValidationRunSummary CandidateSummary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed record TrackInvestigationRequest(
    string? Title = null,
    string? Severity = null,
    string? Owner = null,
    int WindowDays = 30);

public sealed record UpdateInvestigationCaseRequest(
    string? Status = null,
    string? Severity = null,
    string? Owner = null,
    string? RootCause = null,
    string? ResolutionNote = null,
    string? VerificationNote = null,
    string? VerificationRunId = null);

public sealed record InvestigationTrendPoint(
    string Day,
    int Total,
    int ExecutionFailure,
    int QualityNg,
    int PerformanceRegression);

public sealed record InvestigationSignatureTrend(
    string CaseId,
    string SignatureId,
    string Title,
    string Category,
    string Status,
    string Severity,
    string? PrimaryNodeType,
    int Occurrences,
    DateTimeOffset LastSeenAt);

public sealed record InvestigationTrendDashboard(
    int WindowDays,
    int TrackedCases,
    int ActiveCases,
    int VerifiedOrClosedCases,
    int EvidenceOccurrences,
    IReadOnlyList<InvestigationTrendPoint> Timeline,
    IReadOnlyList<InvestigationSignatureTrend> TopSignatures);

/// <summary>
/// V0.63 investigation + verification domain. Cases are intentionally separate from the runtime hot path:
/// they reference persisted trace/signature evidence and provide an engineering lifecycle
/// without changing workflow execution, camera acquisition, robot control or plugin dispatch.
/// </summary>
public sealed class InvestigationCaseService(
    SqliteMetadataDatabase db,
    TraceabilityStore traces,
    TraceAnalysisService analysis)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllowedStatuses = ["Open", "Investigating", "Resolved", "Verified", "Closed"];
    private static readonly string[] AllowedSeverities = ["Low", "Medium", "High", "Critical"];

    public async Task<IReadOnlyList<InvestigationCaseSummary>> ListAsync(int take = 100, string? status = null, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        if (!string.IsNullOrWhiteSpace(status)) ValidateStatus(status);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {CaseColumns} FROM investigation_cases {(string.IsNullOrWhiteSpace(status) ? "" : "WHERE status=$status")} ORDER BY CASE status WHEN 'Open' THEN 0 WHEN 'Investigating' THEN 1 WHEN 'Resolved' THEN 2 WHEN 'Verified' THEN 3 ELSE 4 END,last_seen_at DESC LIMIT $take;";
        if (!string.IsNullOrWhiteSpace(status)) command.Parameters.AddWithValue("$status", CanonicalStatus(status));
        command.Parameters.AddWithValue("$take", take);
        var result = new List<InvestigationCaseSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadCase(reader));
        return result;
    }

    public async Task<InvestigationCaseDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        var item = await GetSummaryAsync(id, ct);
        if (item is null) return null;
        return new InvestigationCaseDetail(item, await ReadEvidenceAsync(id, 100, ct), await ReadVerificationEvidenceAsync(id, 20, ct));
    }

    public async Task<InvestigationCaseDetail?> GetForTraceAsync(string runId, CancellationToken ct = default)
    {
        await using (var connection = await db.OpenConnectionAsync(ct))
        await using (var direct = connection.CreateCommand())
        {
            direct.CommandText = $"SELECT {AliasedCaseColumns("c")} FROM investigation_case_runs r JOIN investigation_cases c ON c.id=r.case_id WHERE r.run_id=$run ORDER BY c.updated_at DESC LIMIT 1;";
            direct.Parameters.AddWithValue("$run", runId);
            await using var reader = await direct.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var summary = ReadCase(reader);
                return new InvestigationCaseDetail(summary, await ReadEvidenceAsync(summary.Id, 100, ct), await ReadVerificationEvidenceAsync(summary.Id, 20, ct));
            }
        }

        var trace = await traces.GetAsync(runId, ct);
        if (trace is null) throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        var signature = await analysis.AnalyzeFailureAsync(runId, days: 30, take: 1, ct);
        if (!signature.HasSignature || string.IsNullOrWhiteSpace(signature.SignatureId)) return null;
        var key = CaseKey(trace, signature.SignatureId);
        await using var lookupConnection = await db.OpenConnectionAsync(ct);
        await using var lookup = lookupConnection.CreateCommand();
        lookup.CommandText = $"SELECT {CaseColumns} FROM investigation_cases WHERE case_key=$key LIMIT 1;";
        lookup.Parameters.AddWithValue("$key", key);
        await using var lookupReader = await lookup.ExecuteReaderAsync(ct);
        if (!await lookupReader.ReadAsync(ct)) return null;
        var found = ReadCase(lookupReader);
        return new InvestigationCaseDetail(found, await ReadEvidenceAsync(found.Id, 100, ct), await ReadVerificationEvidenceAsync(found.Id, 20, ct));
    }

    public async Task<InvestigationCaseDetail> TrackFromTraceAsync(string runId, TrackInvestigationRequest? request, CancellationToken ct = default)
    {
        request ??= new TrackInvestigationRequest();
        var trace = await traces.GetAsync(runId, ct)
            ?? throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        var days = Math.Clamp(request.WindowDays, 1, 3650);
        var signature = await analysis.AnalyzeFailureAsync(runId, days, take: 500, ct);
        if (!signature.HasSignature || string.IsNullOrWhiteSpace(signature.SignatureId))
            throw new ApiValidationException("This trace has no failure signature to track as an investigation case.");

        var requestedSeverity = string.IsNullOrWhiteSpace(request.Severity) ? null : NormalizeSeverity(request.Severity);
        var severity = requestedSeverity ?? DefaultSeverity(signature.Category);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? $"{signature.Category} · {signature.PrimaryNodeType ?? trace.WorkflowName}"
            : request.Title.Trim();
        if (title.Length > 160) throw new ApiValidationException("Investigation title must be <= 160 characters.");
        var owner = NormalizeOptional(request.Owner, 120, "Owner");
        var caseKey = CaseKey(trace, signature.SignatureId);
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<FailureSignatureOccurrence> evidence = signature.RecentOccurrences.Count > 0
            ? signature.RecentOccurrences
            : [new FailureSignatureOccurrence(trace.RunId, trace.StartedAt, trace.Disposition, trace.TotalDurationMs)];
        var firstSeen = evidence.Min(x => x.StartedAt);
        var lastSeen = evidence.Max(x => x.StartedAt);

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        var existing = await ReadByKeyAsync(connection, tx, caseKey, ct);
        var id = existing?.Id ?? $"case-{Guid.NewGuid():N}"[..17];
        var reopen = existing is not null
            && existing.Status is "Resolved" or "Verified" or "Closed"
            && lastSeen > (existing.ClosedAt ?? existing.VerifiedAt ?? existing.ResolvedAt ?? existing.UpdatedAt);
        var status = reopen ? "Open" : existing?.Status ?? "Open";
        var reopenCount = (existing?.ReopenCount ?? 0) + (reopen ? 1 : 0);
        var createdAt = existing?.CreatedAt ?? now;
        var effectiveSeverity = existing is null ? severity : requestedSeverity ?? existing.Severity;
        var effectiveFirstSeen = existing is null || firstSeen < existing.FirstSeenAt ? firstSeen : existing.FirstSeenAt;
        var effectiveLastSeen = existing is null || lastSeen > existing.LastSeenAt ? lastSeen : existing.LastSeenAt;

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
INSERT INTO investigation_cases(
 id,case_key,signature_id,category,title,status,severity,workflow_id,workflow_hash,source,job_id,job_version,
 primary_node_id,primary_node_type,fingerprint,representative_run_id,first_seen_at,last_seen_at,occurrence_count,reopen_count,
 owner,root_cause,resolution_note,verification_note,verification_run_id,resolved_at,verified_at,closed_at,created_at,updated_at)
VALUES(
 $id,$key,$signature,$category,$title,$status,$severity,$workflow,$hash,$source,$job,$jobVersion,
 $nodeId,$nodeType,$fingerprint,$run,$first,$last,$count,$reopens,
 $owner,$rootCause,$resolution,$verification,$verificationRun,$resolvedAt,$verifiedAt,$closedAt,$created,$updated)
ON CONFLICT(case_key) DO UPDATE SET
 category=excluded.category,title=COALESCE(NULLIF(investigation_cases.title,''),excluded.title),
 severity=CASE WHEN investigation_cases.severity='Critical' THEN 'Critical' ELSE excluded.severity END,
 primary_node_id=excluded.primary_node_id,primary_node_type=excluded.primary_node_type,fingerprint=excluded.fingerprint,
 representative_run_id=excluded.representative_run_id,first_seen_at=excluded.first_seen_at,last_seen_at=excluded.last_seen_at,
 occurrence_count=MAX(investigation_cases.occurrence_count,excluded.occurrence_count),reopen_count=excluded.reopen_count,
 owner=COALESCE(excluded.owner,investigation_cases.owner),status=excluded.status,
 verification_note=CASE WHEN excluded.status='Open' THEN NULL ELSE investigation_cases.verification_note END,
 verification_run_id=CASE WHEN excluded.status='Open' THEN NULL ELSE investigation_cases.verification_run_id END,
 resolved_at=CASE WHEN excluded.status='Open' THEN NULL ELSE investigation_cases.resolved_at END,
 verified_at=CASE WHEN excluded.status='Open' THEN NULL ELSE investigation_cases.verified_at END,
 closed_at=CASE WHEN excluded.status='Open' THEN NULL ELSE investigation_cases.closed_at END,
 updated_at=excluded.updated_at;
""";
            Add(upsert, "$id", id); Add(upsert, "$key", caseKey); Add(upsert, "$signature", signature.SignatureId);
            Add(upsert, "$category", signature.Category); Add(upsert, "$title", existing?.Title ?? title); Add(upsert, "$status", status);
            Add(upsert, "$severity", effectiveSeverity); Add(upsert, "$workflow", trace.WorkflowId);
            Add(upsert, "$hash", trace.WorkflowHash); Add(upsert, "$source", trace.Source); Add(upsert, "$job", trace.JobId); Add(upsert, "$jobVersion", trace.JobVersion);
            Add(upsert, "$nodeId", signature.PrimaryNodeId); Add(upsert, "$nodeType", signature.PrimaryNodeType); Add(upsert, "$fingerprint", signature.Fingerprint);
            Add(upsert, "$run", trace.RunId); Add(upsert, "$first", Iso(effectiveFirstSeen)); Add(upsert, "$last", Iso(effectiveLastSeen));
            Add(upsert, "$count", Math.Max(signature.OccurrenceCount, existing?.OccurrenceCount ?? 0)); Add(upsert, "$reopens", reopenCount); Add(upsert, "$owner", owner);
            Add(upsert, "$rootCause", existing?.RootCause); Add(upsert, "$resolution", existing?.ResolutionNote); Add(upsert, "$verification", existing?.VerificationNote);
            Add(upsert, "$verificationRun", existing?.VerificationRunId); Add(upsert, "$resolvedAt", existing?.ResolvedAt is null ? null : Iso(existing.ResolvedAt.Value));
            Add(upsert, "$verifiedAt", existing?.VerifiedAt is null ? null : Iso(existing.VerifiedAt.Value)); Add(upsert, "$closedAt", existing?.ClosedAt is null ? null : Iso(existing.ClosedAt.Value));
            Add(upsert, "$created", Iso(createdAt)); Add(upsert, "$updated", Iso(now));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        // Resolve the canonical id after the unique-key upsert. This closes the race where
        // two engineers track the same signature concurrently and only one provisional id wins.
        await using (var resolve = connection.CreateCommand())
        {
            resolve.Transaction = tx;
            resolve.CommandText = "SELECT id FROM investigation_cases WHERE case_key=$key;";
            resolve.Parameters.AddWithValue("$key", caseKey);
            id = Convert.ToString(await resolve.ExecuteScalarAsync(ct))
                ?? throw new ApiConflictException("Investigation case identity could not be resolved after upsert.");
        }

        foreach (var occurrence in evidence)
        {
            await using var link = connection.CreateCommand();
            link.Transaction = tx;
            link.CommandText = """
INSERT INTO investigation_case_runs(case_id,run_id,started_at,disposition,total_duration_ms,node_duration_ms,linked_at)
VALUES($case,$run,$started,$disposition,$duration,$nodeDuration,$linked)
ON CONFLICT(case_id,run_id) DO UPDATE SET node_duration_ms=COALESCE(excluded.node_duration_ms,investigation_case_runs.node_duration_ms);
""";
            Add(link, "$case", id); Add(link, "$run", occurrence.RunId); Add(link, "$started", Iso(occurrence.StartedAt));
            Add(link, "$disposition", occurrence.Disposition); Add(link, "$duration", occurrence.TotalDurationMs); Add(link, "$nodeDuration", occurrence.NodeDurationMs);
            Add(link, "$linked", Iso(now));
            await link.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<InvestigationCaseDetail> UpdateAsync(string id, UpdateInvestigationCaseRequest request, CancellationToken ct = default)
    {
        var existing = await GetSummaryAsync(id, ct) ?? throw new ApiNotFoundException($"Investigation case '{id}' was not found.");
        var status = string.IsNullOrWhiteSpace(request.Status) ? existing.Status : CanonicalStatus(request.Status);
        ValidateTransition(existing.Status, status);
        var severity = string.IsNullOrWhiteSpace(request.Severity) ? existing.Severity : NormalizeSeverity(request.Severity);
        var owner = request.Owner is null ? existing.Owner : NormalizeOptional(request.Owner, 120, "Owner");
        var rootCause = request.RootCause is null ? existing.RootCause : NormalizeOptional(request.RootCause, 2000, "Root cause");
        var resolution = request.ResolutionNote is null ? existing.ResolutionNote : NormalizeOptional(request.ResolutionNote, 4000, "Resolution note");
        var verification = request.VerificationNote is null ? existing.VerificationNote : NormalizeOptional(request.VerificationNote, 4000, "Verification note");
        var verificationRun = request.VerificationRunId is null ? existing.VerificationRunId : NormalizeOptional(request.VerificationRunId, 128, "Verification run id");
        if (status == "Resolved" && string.IsNullOrWhiteSpace(resolution)) throw new ApiValidationException("Resolution note is required before marking a case Resolved.");
        InvestigationVerificationEvidence? passedEvidence = null;
        if (status == "Verified")
        {
            if (string.IsNullOrWhiteSpace(verification)) throw new ApiValidationException("Verification note is required before marking a case Verified.");
            passedEvidence = await GetLatestPassedVerificationEvidenceAsync(id, existing.ResolvedAt, ct);
            if (passedEvidence is null)
                throw new ApiConflictException("A fresh Passed dataset regression gate is required after the latest resolution before a case can be Verified.");
            verificationRun = passedEvidence.CandidateValidationRunId;
        }
        if (status == "Closed" && existing.Status != "Verified") throw new ApiConflictException("A case must be Verified before it can be Closed.");

        var now = DateTimeOffset.UtcNow;
        var resolvedAt = status == "Resolved" && existing.Status != "Resolved" ? now : existing.ResolvedAt;
        var verifiedAt = status == "Verified" && existing.Status != "Verified" ? now : existing.VerifiedAt;
        var closedAt = status == "Closed" && existing.Status != "Closed" ? now : existing.ClosedAt;
        if (status is "Open" or "Investigating") { resolvedAt = null; verifiedAt = null; closedAt = null; }
        if (status == "Open") { verification = null; verificationRun = null; }
        if (status == "Resolved") { verifiedAt = null; closedAt = null; }
        if (status == "Verified") closedAt = null;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE investigation_cases SET status=$status,severity=$severity,owner=$owner,root_cause=$rootCause,resolution_note=$resolution,
 verification_note=$verification,verification_run_id=$verificationRun,resolved_at=$resolvedAt,verified_at=$verifiedAt,closed_at=$closedAt,updated_at=$updated
WHERE id=$id;
""";
        Add(command, "$status", status); Add(command, "$severity", severity); Add(command, "$owner", owner); Add(command, "$rootCause", rootCause);
        Add(command, "$resolution", resolution); Add(command, "$verification", verification); Add(command, "$verificationRun", verificationRun);
        Add(command, "$resolvedAt", resolvedAt is null ? null : Iso(resolvedAt.Value)); Add(command, "$verifiedAt", verifiedAt is null ? null : Iso(verifiedAt.Value));
        Add(command, "$closedAt", closedAt is null ? null : Iso(closedAt.Value)); Add(command, "$updated", Iso(now)); Add(command, "$id", id);
        await command.ExecuteNonQueryAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<IReadOnlyList<InvestigationVerificationEvidence>> GetVerificationEvidenceAsync(string id, CancellationToken ct = default)
    {
        _ = await GetSummaryAsync(id, ct) ?? throw new ApiNotFoundException($"Investigation case '{id}' was not found.");
        return await ReadVerificationEvidenceAsync(id, 100, ct);
    }

    public async Task<InvestigationVerificationEvidence> EvaluateVerificationAsync(
        string id,
        CreateInvestigationVerificationRequest request,
        CancellationToken ct = default)
    {
        var investigation = await GetSummaryAsync(id, ct)
            ?? throw new ApiNotFoundException($"Investigation case '{id}' was not found.");
        if (investigation.Status != "Resolved")
            throw new ApiConflictException("Dataset verification evidence can only be evaluated while a case is Resolved.");

        var datasetId = (request.DatasetId ?? string.Empty).Trim();
        var baselineId = (request.BaselineValidationRunId ?? string.Empty).Trim();
        var candidateId = (request.CandidateValidationRunId ?? string.Empty).Trim();
        if (datasetId.Length == 0 || baselineId.Length == 0 || candidateId.Length == 0)
            throw new ApiValidationException("DatasetId, BaselineValidationRunId and CandidateValidationRunId are required.");
        if (baselineId.Equals(candidateId, StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("Baseline and candidate validation runs must be different.");
        var note = NormalizeOptional(request.Note, 2000, "Verification evidence note");

        var baseline = await ReadValidationEvidenceSnapshotAsync(baselineId, ct);
        var candidate = await ReadValidationEvidenceSnapshotAsync(candidateId, ct);
        if (!baseline.DatasetId.Equals(datasetId, StringComparison.OrdinalIgnoreCase) || !candidate.DatasetId.Equals(datasetId, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("Baseline and candidate validation runs must both belong to the selected dataset.");
        if (!baseline.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || !candidate.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("Both validation runs must be Completed before verification evidence can be evaluated.");
        if (baseline.Summary is null || candidate.Summary is null)
            throw new ApiConflictException("Both validation runs must contain completed summary evidence.");

        var reasons = new List<string>();
        var warnings = new List<string>();
        if (baseline.RequestedCount != candidate.RequestedCount)
            reasons.Add($"Coverage mismatch: baseline requested {baseline.RequestedCount}, candidate requested {candidate.RequestedCount}.");
        if (baseline.CompletedCount != baseline.RequestedCount || candidate.CompletedCount != candidate.RequestedCount)
            reasons.Add("Both A/B runs must complete every requested dataset item.");
        if (candidate.Summary.Errors > 0)
            reasons.Add($"Candidate contains {candidate.Summary.Errors} execution error(s); verification requires zero errors.");
        if (candidate.Summary.FalseOk > baseline.Summary.FalseOk)
            reasons.Add($"False OK regressed from {baseline.Summary.FalseOk} to {candidate.Summary.FalseOk}.");
        if (candidate.Summary.FalseNg > baseline.Summary.FalseNg)
            reasons.Add($"False NG regressed from {baseline.Summary.FalseNg} to {candidate.Summary.FalseNg}.");
        if (candidate.Summary.Accuracy + 1e-9 < baseline.Summary.Accuracy)
            reasons.Add($"Accuracy regressed from {baseline.Summary.Accuracy:P2} to {candidate.Summary.Accuracy:P2}.");
        if (baseline.WorkflowHash.Equals(candidate.WorkflowHash, StringComparison.OrdinalIgnoreCase))
            warnings.Add("Baseline and candidate workflow hashes are identical; the fix may live outside workflow JSON (plugin/model/runtime dependency). Confirm provenance separately.");

        var baselineScan = await CountSignatureHitsAsync(baseline.ReplayRunIds, investigation.SignatureId, ct);
        var candidateScan = await CountSignatureHitsAsync(candidate.ReplayRunIds, investigation.SignatureId, ct);
        var inconclusive = new List<string>();
        if (baselineScan.Inspectable == 0)
            inconclusive.Add("Baseline validation produced no inspectable replay traces for signature analysis.");
        else if (baselineScan.Hits == 0)
            inconclusive.Add("Selected baseline dataset does not reproduce the investigation signature, so it cannot prove the fix.");
        if (candidateScan.Inspectable < candidate.CompletedCount)
            inconclusive.Add($"Only {candidateScan.Inspectable}/{candidate.CompletedCount} candidate results have inspectable replay traces.");
        if (candidateScan.Hits > 0)
            reasons.Add($"Original signature still appears in {candidateScan.Hits} candidate replay result(s).");

        var gate = reasons.Count > 0 ? "Failed" : inconclusive.Count > 0 ? "Inconclusive" : "Passed";
        if (inconclusive.Count > 0) warnings.AddRange(inconclusive);
        var now = DateTimeOffset.UtcNow;
        var evidenceId = $"verify-{Guid.NewGuid():N}"[..19];

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO investigation_case_verifications(
 id,case_id,dataset_id,baseline_validation_run_id,candidate_validation_run_id,baseline_workflow_hash,candidate_workflow_hash,
 gate_status,baseline_signature_hits,candidate_signature_hits,baseline_inspectable_results,candidate_inspectable_results,
 baseline_summary_json,candidate_summary_json,reasons_json,warnings_json,note,created_at)
VALUES($id,$case,$dataset,$baseline,$candidate,$baselineHash,$candidateHash,$gate,$baselineHits,$candidateHits,$baselineInspectable,$candidateInspectable,$baselineSummary,$candidateSummary,$reasons,$warnings,$note,$created);
""";
        Add(command, "$id", evidenceId); Add(command, "$case", id); Add(command, "$dataset", datasetId);
        Add(command, "$baseline", baselineId); Add(command, "$candidate", candidateId); Add(command, "$baselineHash", baseline.WorkflowHash); Add(command, "$candidateHash", candidate.WorkflowHash);
        Add(command, "$gate", gate); Add(command, "$baselineHits", baselineScan.Hits); Add(command, "$candidateHits", candidateScan.Hits);
        Add(command, "$baselineInspectable", baselineScan.Inspectable); Add(command, "$candidateInspectable", candidateScan.Inspectable);
        Add(command, "$baselineSummary", JsonSerializer.Serialize(baseline.Summary, _json)); Add(command, "$candidateSummary", JsonSerializer.Serialize(candidate.Summary, _json));
        Add(command, "$reasons", JsonSerializer.Serialize(reasons, _json)); Add(command, "$warnings", JsonSerializer.Serialize(warnings, _json));
        Add(command, "$note", note); Add(command, "$created", Iso(now));
        await command.ExecuteNonQueryAsync(ct);

        return new InvestigationVerificationEvidence(
            evidenceId, id, datasetId, baseline.DatasetName, baselineId, candidateId,
            baseline.WorkflowHash, candidate.WorkflowHash, gate,
            baselineScan.Hits, candidateScan.Hits, baselineScan.Inspectable, candidateScan.Inspectable,
            baseline.Summary, candidate.Summary, reasons, warnings, note, now);
    }

    public async Task<InvestigationTrendDashboard> TrendsAsync(int days = 30, int top = 8, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650); top = Math.Clamp(top, 1, 50);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("O");
        await using var connection = await db.OpenConnectionAsync(ct);

        int trackedCases;
        int activeCases;
        int verifiedClosed;
        await using (var counts = connection.CreateCommand())
        {
            counts.CommandText = "SELECT COUNT(*),SUM(CASE WHEN status IN ('Open','Investigating','Resolved') THEN 1 ELSE 0 END),SUM(CASE WHEN status IN ('Verified','Closed') THEN 1 ELSE 0 END) FROM investigation_cases;";
            await using var reader = await counts.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            trackedCases = reader.GetInt32(0);
            activeCases = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            verifiedClosed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }

        var timeline = new List<InvestigationTrendPoint>();
        var totalEvidence = 0;
        await using (var trend = connection.CreateCommand())
        {
            trend.CommandText = """
SELECT substr(r.started_at,1,10),COUNT(*),
 SUM(CASE WHEN c.category='ExecutionFailure' THEN 1 ELSE 0 END),
 SUM(CASE WHEN c.category='QualityNG' THEN 1 ELSE 0 END),
 SUM(CASE WHEN c.category='PerformanceRegression' THEN 1 ELSE 0 END)
FROM investigation_case_runs r JOIN investigation_cases c ON c.id=r.case_id
WHERE r.started_at >= $cutoff
GROUP BY substr(r.started_at,1,10) ORDER BY substr(r.started_at,1,10);
""";
            trend.Parameters.AddWithValue("$cutoff", cutoff);
            await using var reader = await trend.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var total = reader.GetInt32(1); totalEvidence += total;
                timeline.Add(new InvestigationTrendPoint(reader.GetString(0), total, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        var topSignatures = new List<InvestigationSignatureTrend>();
        await using (var topQuery = connection.CreateCommand())
        {
            topQuery.CommandText = """
SELECT c.id,c.signature_id,c.title,c.category,c.status,c.severity,c.primary_node_type,COUNT(r.run_id),MAX(r.started_at)
FROM investigation_cases c LEFT JOIN investigation_case_runs r ON r.case_id=c.id AND r.started_at >= $cutoff
GROUP BY c.id,c.signature_id,c.title,c.category,c.status,c.severity,c.primary_node_type
ORDER BY COUNT(r.run_id) DESC,c.last_seen_at DESC LIMIT $top;
""";
            topQuery.Parameters.AddWithValue("$cutoff", cutoff); topQuery.Parameters.AddWithValue("$top", top);
            await using var reader = await topQuery.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                topSignatures.Add(new InvestigationSignatureTrend(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7), reader.IsDBNull(8) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(reader.GetString(8))));
        }
        return new InvestigationTrendDashboard(days, trackedCases, activeCases, verifiedClosed, totalEvidence, timeline, topSignatures);
    }

    private async Task<InvestigationCaseSummary?> GetSummaryAsync(string id, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {CaseColumns} FROM investigation_cases WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCase(reader) : null;
    }

    private static async Task<InvestigationCaseSummary?> ReadByKeyAsync(SqliteConnection connection, SqliteTransaction tx, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"SELECT {CaseColumns} FROM investigation_cases WHERE case_key=$key;"; command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCase(reader) : null;
    }

    private async Task<IReadOnlyList<InvestigationVerificationEvidence>> ReadVerificationEvidenceAsync(string id, int take, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT v.id,v.case_id,v.dataset_id,d.name,v.baseline_validation_run_id,v.candidate_validation_run_id,
 v.baseline_workflow_hash,v.candidate_workflow_hash,v.gate_status,v.baseline_signature_hits,v.candidate_signature_hits,
 v.baseline_inspectable_results,v.candidate_inspectable_results,v.baseline_summary_json,v.candidate_summary_json,
 v.reasons_json,v.warnings_json,v.note,v.created_at
FROM investigation_case_verifications v JOIN validation_datasets d ON d.id=v.dataset_id
WHERE v.case_id=$id ORDER BY v.created_at DESC LIMIT $take;
""";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 500));
        var result = new List<InvestigationVerificationEvidence>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var baseline = JsonSerializer.Deserialize<ValidationRunSummary>(reader.GetString(13), _json)
                ?? throw new ApiConflictException("Stored baseline validation summary is invalid.");
            var candidate = JsonSerializer.Deserialize<ValidationRunSummary>(reader.GetString(14), _json)
                ?? throw new ApiConflictException("Stored candidate validation summary is invalid.");
            result.Add(new InvestigationVerificationEvidence(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12),
                baseline, candidate,
                JsonSerializer.Deserialize<string[]>(reader.GetString(15), _json) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(16), _json) ?? [],
                reader.IsDBNull(17) ? null : reader.GetString(17), DateTimeOffset.Parse(reader.GetString(18))));
        }
        return result;
    }

    private async Task<InvestigationVerificationEvidence?> GetLatestPassedVerificationEvidenceAsync(string id, DateTimeOffset? resolvedAt, CancellationToken ct)
    {
        if (resolvedAt is null) return null;
        var evidence = await ReadVerificationEvidenceAsync(id, 100, ct);
        return evidence.FirstOrDefault(x => x.GateStatus == "Passed" && x.CreatedAt >= resolvedAt.Value);
    }

    private async Task<ValidationEvidenceSnapshot> ReadValidationEvidenceSnapshotAsync(string runId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        string? datasetId = null, datasetName = null, status = null, workflowHash = null, summaryJson = null;
        var requested = 0; var completed = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT r.dataset_id,d.name,r.status,r.workflow_hash,r.requested_count,r.completed_count,r.summary_json
FROM validation_runs r JOIN validation_datasets d ON d.id=r.dataset_id WHERE r.run_id=$run;
""";
            command.Parameters.AddWithValue("$run", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ApiNotFoundException($"Validation run '{runId}' was not found.");
            datasetId = reader.GetString(0); datasetName = reader.GetString(1); status = reader.GetString(2); workflowHash = reader.GetString(3);
            requested = reader.GetInt32(4); completed = reader.GetInt32(5); summaryJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        }
        var replayIds = new List<string>();
        await using (var results = connection.CreateCommand())
        {
            results.CommandText = "SELECT replay_run_id FROM validation_results WHERE run_id=$run AND replay_run_id IS NOT NULL ORDER BY item_id;";
            results.Parameters.AddWithValue("$run", runId);
            await using var reader = await results.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) replayIds.Add(reader.GetString(0));
        }
        var summary = summaryJson is null ? null : JsonSerializer.Deserialize<ValidationRunSummary>(summaryJson, _json);
        return new ValidationEvidenceSnapshot(runId, datasetId!, datasetName!, status!, workflowHash!, requested, completed, summary, replayIds);
    }

    private async Task<(int Hits, int Inspectable)> CountSignatureHitsAsync(IReadOnlyList<string> replayRunIds, string signatureId, CancellationToken ct)
    {
        var hits = 0; var inspectable = 0;
        foreach (var replayRunId in replayRunIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var signature = await analysis.AnalyzeFailureAsync(replayRunId, 3650, 1, ct);
                inspectable++;
                if (signature.HasSignature && string.Equals(signature.SignatureId, signatureId, StringComparison.Ordinal)) hits++;
            }
            catch (ApiNotFoundException)
            {
                // Old validation rows can outlive sampled/retained replay traces. Missing replay evidence makes the gate inconclusive, never passed.
            }
        }
        return (hits, inspectable);
    }

    private sealed record ValidationEvidenceSnapshot(
        string RunId,
        string DatasetId,
        string DatasetName,
        string Status,
        string WorkflowHash,
        int RequestedCount,
        int CompletedCount,
        ValidationRunSummary? Summary,
        IReadOnlyList<string> ReplayRunIds);

    private async Task<IReadOnlyList<InvestigationCaseRun>> ReadEvidenceAsync(string id, int take, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,started_at,disposition,total_duration_ms,node_duration_ms FROM investigation_case_runs WHERE case_id=$id ORDER BY started_at DESC LIMIT $take;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 500));
        var result = new List<InvestigationCaseRun>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new InvestigationCaseRun(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        return result;
    }

    private static InvestigationCaseSummary ReadCase(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt32(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13), r.GetString(14),
        DateTimeOffset.Parse(r.GetString(15)), DateTimeOffset.Parse(r.GetString(16)), r.GetInt32(17), r.GetInt32(18),
        r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21),
        r.IsDBNull(22) ? null : r.GetString(22), r.IsDBNull(23) ? null : r.GetString(23),
        r.IsDBNull(24) ? null : DateTimeOffset.Parse(r.GetString(24)), r.IsDBNull(25) ? null : DateTimeOffset.Parse(r.GetString(25)), r.IsDBNull(26) ? null : DateTimeOffset.Parse(r.GetString(26)),
        DateTimeOffset.Parse(r.GetString(27)), DateTimeOffset.Parse(r.GetString(28)));

    private static string CaseKey(RunTraceRecord trace, string signatureId)
    {
        var identity = string.IsNullOrWhiteSpace(trace.WorkflowHash) ? $"id:{trace.WorkflowId}" : $"hash:{trace.WorkflowHash}";
        var raw = $"{identity}|{trace.Source}|{trace.JobId ?? "adhoc"}|{trace.JobVersion?.ToString() ?? "-"}|{signatureId}".ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..24];
    }

    private static void ValidateTransition(string from, string to)
    {
        if (from == to) return;
        var allowed = from switch
        {
            "Open" => new[] { "Investigating" },
            "Investigating" => new[] { "Open", "Resolved" },
            "Resolved" => new[] { "Investigating", "Verified" },
            "Verified" => new[] { "Open", "Closed" },
            "Closed" => new[] { "Open" },
            _ => Array.Empty<string>()
        };
        if (!allowed.Contains(to, StringComparer.OrdinalIgnoreCase))
            throw new ApiConflictException($"Invalid investigation transition {from} → {to}.");
    }

    private static string CanonicalStatus(string value)
    {
        var found = AllowedStatuses.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found is null) throw new ApiValidationException($"Status must be one of: {string.Join(", ", AllowedStatuses)}.");
        return found;
    }
    private static void ValidateStatus(string value) => _ = CanonicalStatus(value);
    private static string NormalizeSeverity(string value)
    {
        var found = AllowedSeverities.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found is null) throw new ApiValidationException($"Severity must be one of: {string.Join(", ", AllowedSeverities)}.");
        return found;
    }
    private static string DefaultSeverity(string category) => category switch { "ExecutionFailure" => "High", "QualityNG" => "High", "PerformanceRegression" => "Medium", _ => "Medium" };
    private static string? NormalizeOptional(string? value, int max, string field)
    {
        if (value is null) return null; var trimmed = value.Trim(); if (trimmed.Length == 0) return null;
        if (trimmed.Length > max) throw new ApiValidationException($"{field} must be <= {max} characters."); return trimmed;
    }
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private const string CaseColumns = "id,signature_id,category,title,status,severity,workflow_id,workflow_hash,source,job_id,job_version,primary_node_id,primary_node_type,fingerprint,representative_run_id,first_seen_at,last_seen_at,occurrence_count,reopen_count,owner,root_cause,resolution_note,verification_note,verification_run_id,resolved_at,verified_at,closed_at,created_at,updated_at";
    private static string AliasedCaseColumns(string a) => string.Join(",", CaseColumns.Split(',').Select(x => $"{a}.{x}"));
}
