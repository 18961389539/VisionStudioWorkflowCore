using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

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
    IVisionStudioDbFactory dbFactory,
    TraceabilityStore traces,
    TraceAnalysisService analysis)
{
    public InvestigationCaseService(SqliteMetadataDatabase db, TraceabilityStore traces, TraceAnalysisService analysis)
        : this(new VisionStudioDbFactory(db), traces, analysis) { }

    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllowedStatuses = ["Open", "Investigating", "Resolved", "Verified", "Closed"];
    private static readonly string[] AllowedSeverities = ["Low", "Medium", "High", "Critical"];

    public async Task<IReadOnlyList<InvestigationCaseSummary>> ListAsync(int take = 100, string? status = null, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        if (!string.IsNullOrWhiteSpace(status)) ValidateStatus(status);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<InvestigationCaseEntity> query = db.InvestigationCases;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var canonical = CanonicalStatus(status);
            query = query.Where(x => x.Status == canonical);
        }
        var entities = await query
            .OrderBy(x => x.Status == "Open" ? 0 : x.Status == "Investigating" ? 1 : x.Status == "Resolved" ? 2 : x.Status == "Verified" ? 3 : 4)
            .ThenByDescending(x => x.LastSeenAt)
            .Take(take)
            .ToListAsync(ct);
        return entities.Select(ToSummary).ToList();
    }

    public async Task<InvestigationCaseDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        var item = await GetSummaryAsync(id, ct);
        if (item is null) return null;
        return new InvestigationCaseDetail(item, await ReadEvidenceAsync(id, 100, ct), await ReadVerificationEvidenceAsync(id, 20, ct));
    }

    public async Task<InvestigationCaseDetail?> GetForTraceAsync(string runId, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var direct = await (
                from r in db.InvestigationCaseRuns
                join c in db.InvestigationCases on r.CaseId equals c.Id
                where r.RunId == runId
                orderby c.UpdatedAt descending
                select c)
                .FirstOrDefaultAsync(ct);
            if (direct is not null)
            {
                var summary = ToSummary(direct);
                return new InvestigationCaseDetail(summary, await ReadEvidenceAsync(summary.Id, 100, ct), await ReadVerificationEvidenceAsync(summary.Id, 20, ct));
            }
        }

        var trace = await traces.GetAsync(runId, ct);
        if (trace is null) throw new ApiNotFoundException($"Trace '{runId}' was not found.");
        var signature = await analysis.AnalyzeFailureAsync(runId, days: 30, take: 1, ct);
        if (!signature.HasSignature || string.IsNullOrWhiteSpace(signature.SignatureId)) return null;
        var key = CaseKey(trace, signature.SignatureId);
        await using var lookupDb = await dbFactory.CreateDbContextAsync(ct);
        var found = await lookupDb.InvestigationCases.FirstOrDefaultAsync(x => x.CaseKey == key, ct);
        if (found is null) return null;
        var summaryFound = ToSummary(found);
        return new InvestigationCaseDetail(summaryFound, await ReadEvidenceAsync(summaryFound.Id, 100, ct), await ReadVerificationEvidenceAsync(summaryFound.Id, 20, ct));
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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var existing = await db.InvestigationCases.AsTracking().FirstOrDefaultAsync(x => x.CaseKey == caseKey, ct);
        var id = existing?.Id ?? $"case-{Guid.NewGuid():N}"[..17];
        var reopen = existing is not null
            && existing.Status is "Resolved" or "Verified" or "Closed"
            && lastSeen > (existing.ClosedAt ?? existing.VerifiedAt ?? existing.ResolvedAt ?? existing.UpdatedAt);
        var status = reopen ? "Open" : existing?.Status ?? "Open";
        var reopenCount = (existing?.ReopenCount ?? 0) + (reopen ? 1 : 0);
        var effectiveSeverity = existing is null
            ? severity
            : string.Equals(existing.Severity, "Critical", StringComparison.Ordinal)
                ? "Critical"
                : requestedSeverity ?? existing.Severity;
        var effectiveFirstSeen = existing is null || firstSeen < existing.FirstSeenAt ? firstSeen : existing.FirstSeenAt;
        var effectiveLastSeen = existing is null || lastSeen > existing.LastSeenAt ? lastSeen : existing.LastSeenAt;

        if (existing is null)
        {
            existing = new InvestigationCaseEntity
            {
                Id = id,
                CaseKey = caseKey,
                SignatureId = signature.SignatureId,
                Category = signature.Category,
                Title = title,
                Status = status,
                Severity = effectiveSeverity,
                WorkflowId = trace.WorkflowId,
                WorkflowHash = trace.WorkflowHash,
                Source = trace.Source,
                JobId = trace.JobId,
                JobVersion = trace.JobVersion,
                PrimaryNodeId = signature.PrimaryNodeId,
                PrimaryNodeType = signature.PrimaryNodeType,
                Fingerprint = signature.Fingerprint,
                RepresentativeRunId = trace.RunId,
                FirstSeenAt = effectiveFirstSeen,
                LastSeenAt = effectiveLastSeen,
                OccurrenceCount = signature.OccurrenceCount,
                ReopenCount = reopenCount,
                Owner = owner,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.InvestigationCases.Add(existing);
        }
        else
        {
            existing.Category = signature.Category;
            if (string.IsNullOrEmpty(existing.Title)) existing.Title = title;
            existing.Severity = effectiveSeverity;
            existing.PrimaryNodeId = signature.PrimaryNodeId;
            existing.PrimaryNodeType = signature.PrimaryNodeType;
            existing.Fingerprint = signature.Fingerprint;
            existing.RepresentativeRunId = trace.RunId;
            existing.FirstSeenAt = effectiveFirstSeen;
            existing.LastSeenAt = effectiveLastSeen;
            existing.OccurrenceCount = Math.Max(existing.OccurrenceCount, signature.OccurrenceCount);
            existing.ReopenCount = reopenCount;
            existing.Owner = owner ?? existing.Owner;
            existing.Status = status;
            if (status == "Open")
            {
                existing.VerificationNote = null;
                existing.VerificationRunId = null;
                existing.ResolvedAt = null;
                existing.VerifiedAt = null;
                existing.ClosedAt = null;
            }
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        var runIds = evidence.Select(x => x.RunId).Distinct().ToList();
        var existingLinks = await db.InvestigationCaseRuns
            .AsTracking()
            .Where(x => x.CaseId == id && runIds.Contains(x.RunId))
            .ToDictionaryAsync(x => x.RunId, ct);

        foreach (var occurrence in evidence)
        {
            if (existingLinks.TryGetValue(occurrence.RunId, out var linked))
            {
                if (occurrence.NodeDurationMs is not null) linked.NodeDurationMs = occurrence.NodeDurationMs;
            }
            else
            {
                var entity = new InvestigationCaseRunEntity
                {
                    CaseId = id,
                    RunId = occurrence.RunId,
                    StartedAt = occurrence.StartedAt,
                    Disposition = occurrence.Disposition,
                    TotalDurationMs = occurrence.TotalDurationMs,
                    NodeDurationMs = occurrence.NodeDurationMs,
                    LinkedAt = now
                };
                db.InvestigationCaseRuns.Add(entity);
                existingLinks[occurrence.RunId] = entity;
            }
        }
        await db.SaveChangesAsync(ct);
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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.InvestigationCases
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.Severity, severity)
                .SetProperty(x => x.Owner, owner)
                .SetProperty(x => x.RootCause, rootCause)
                .SetProperty(x => x.ResolutionNote, resolution)
                .SetProperty(x => x.VerificationNote, verification)
                .SetProperty(x => x.VerificationRunId, verificationRun)
                .SetProperty(x => x.ResolvedAt, resolvedAt)
                .SetProperty(x => x.VerifiedAt, verifiedAt)
                .SetProperty(x => x.ClosedAt, closedAt)
                .SetProperty(x => x.UpdatedAt, now), ct);
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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.InvestigationCaseVerifications.Add(new InvestigationCaseVerificationEntity
        {
            Id = evidenceId,
            CaseId = id,
            DatasetId = datasetId,
            BaselineValidationRunId = baselineId,
            CandidateValidationRunId = candidateId,
            BaselineWorkflowHash = baseline.WorkflowHash,
            CandidateWorkflowHash = candidate.WorkflowHash,
            GateStatus = gate,
            BaselineSignatureHits = baselineScan.Hits,
            CandidateSignatureHits = candidateScan.Hits,
            BaselineInspectableResults = baselineScan.Inspectable,
            CandidateInspectableResults = candidateScan.Inspectable,
            BaselineSummaryJson = JsonSerializer.Serialize(baseline.Summary, _json),
            CandidateSummaryJson = JsonSerializer.Serialize(candidate.Summary, _json),
            ReasonsJson = JsonSerializer.Serialize(reasons, _json),
            WarningsJson = JsonSerializer.Serialize(warnings, _json),
            Note = note,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        return new InvestigationVerificationEvidence(
            evidenceId, id, datasetId, baseline.DatasetName, baselineId, candidateId,
            baseline.WorkflowHash, candidate.WorkflowHash, gate,
            baselineScan.Hits, candidateScan.Hits, baselineScan.Inspectable, candidateScan.Inspectable,
            baseline.Summary, candidate.Summary, reasons, warnings, note, now);
    }

    public async Task<InvestigationTrendDashboard> TrendsAsync(int days = 30, int top = 8, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650); top = Math.Clamp(top, 1, 50);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var statuses = await db.InvestigationCases.Select(x => x.Status).ToListAsync(ct);
        var trackedCases = statuses.Count;
        var activeCases = statuses.Count(s => s is "Open" or "Investigating" or "Resolved");
        var verifiedClosed = statuses.Count(s => s is "Verified" or "Closed");

        var runRows = await (
            from r in db.InvestigationCaseRuns
            join c in db.InvestigationCases on r.CaseId equals c.Id
            where r.StartedAt >= cutoff
            select new { r.CaseId, r.RunId, r.StartedAt, c.Category })
            .ToListAsync(ct);

        var timeline = runRows
            .GroupBy(x => x.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new InvestigationTrendPoint(
                g.Key,
                g.Count(),
                g.Count(x => x.Category == "ExecutionFailure"),
                g.Count(x => x.Category == "QualityNG"),
                g.Count(x => x.Category == "PerformanceRegression")))
            .ToList();
        var totalEvidence = runRows.Count;

        var byCase = runRows.GroupBy(x => x.CaseId).ToDictionary(
            g => g.Key,
            g => new { Count = g.Count(), MaxStarted = g.Max(x => x.StartedAt) });

        var allCases = await db.InvestigationCases.ToListAsync(ct);
        var topSignatures = allCases
            .Select(c =>
            {
                var has = byCase.TryGetValue(c.Id, out var stats);
                return new
                {
                    Trend = new InvestigationSignatureTrend(
                        c.Id, c.SignatureId, c.Title, c.Category, c.Status, c.Severity, c.PrimaryNodeType,
                        has ? stats!.Count : 0,
                        has ? stats!.MaxStarted : DateTimeOffset.MinValue),
                    c.LastSeenAt
                };
            })
            .OrderByDescending(x => x.Trend.Occurrences)
            .ThenByDescending(x => x.LastSeenAt)
            .Take(top)
            .Select(x => x.Trend)
            .ToList();

        return new InvestigationTrendDashboard(days, trackedCases, activeCases, verifiedClosed, totalEvidence, timeline, topSignatures);
    }

    private async Task<InvestigationCaseSummary?> GetSummaryAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.InvestigationCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        return entity is null ? null : ToSummary(entity);
    }

    private async Task<IReadOnlyList<InvestigationVerificationEvidence>> ReadVerificationEvidenceAsync(string id, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await (
            from v in db.InvestigationCaseVerifications
            join d in db.ValidationDatasets on v.DatasetId equals d.Id
            where v.CaseId == id
            orderby v.CreatedAt descending
            select new { Verification = v, DatasetName = d.Name })
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

        var result = new List<InvestigationVerificationEvidence>(rows.Count);
        foreach (var row in rows)
        {
            var v = row.Verification;
            var baseline = JsonSerializer.Deserialize<ValidationRunSummary>(v.BaselineSummaryJson, _json)
                ?? throw new ApiConflictException("Stored baseline validation summary is invalid.");
            var candidate = JsonSerializer.Deserialize<ValidationRunSummary>(v.CandidateSummaryJson, _json)
                ?? throw new ApiConflictException("Stored candidate validation summary is invalid.");
            result.Add(new InvestigationVerificationEvidence(
                v.Id, v.CaseId, v.DatasetId, row.DatasetName, v.BaselineValidationRunId, v.CandidateValidationRunId,
                v.BaselineWorkflowHash, v.CandidateWorkflowHash, v.GateStatus, v.BaselineSignatureHits, v.CandidateSignatureHits,
                v.BaselineInspectableResults, v.CandidateInspectableResults, baseline, candidate,
                JsonSerializer.Deserialize<string[]>(v.ReasonsJson, _json) ?? [],
                JsonSerializer.Deserialize<string[]>(v.WarningsJson, _json) ?? [],
                v.Note, v.CreatedAt));
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await (
            from r in db.ValidationRuns
            join d in db.ValidationDatasets on r.DatasetId equals d.Id
            where r.RunId == runId
            select new { r.DatasetId, DatasetName = d.Name, r.Status, r.WorkflowHash, r.RequestedCount, r.CompletedCount, r.SummaryJson })
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiNotFoundException($"Validation run '{runId}' was not found.");

        var replayIds = await db.ValidationResults
            .Where(x => x.RunId == runId && x.ReplayRunId != null)
            .OrderBy(x => x.ItemId)
            .Select(x => x.ReplayRunId!)
            .ToListAsync(ct);

        var summary = run.SummaryJson is null ? null : JsonSerializer.Deserialize<ValidationRunSummary>(run.SummaryJson, _json);
        return new ValidationEvidenceSnapshot(runId, run.DatasetId, run.DatasetName, run.Status, run.WorkflowHash, run.RequestedCount, run.CompletedCount, summary, replayIds);
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.InvestigationCaseRuns
            .Where(x => x.CaseId == id)
            .OrderByDescending(x => x.StartedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new InvestigationCaseRun(x.RunId, x.StartedAt, x.Disposition, x.TotalDurationMs, x.NodeDurationMs))
            .ToListAsync(ct);
    }

    private static InvestigationCaseSummary ToSummary(InvestigationCaseEntity e) => new(
        e.Id, e.SignatureId, e.Category, e.Title, e.Status, e.Severity, e.WorkflowId,
        e.WorkflowHash, e.Source, e.JobId, e.JobVersion, e.PrimaryNodeId, e.PrimaryNodeType,
        e.Fingerprint, e.RepresentativeRunId, e.FirstSeenAt, e.LastSeenAt, e.OccurrenceCount,
        e.ReopenCount, e.Owner, e.RootCause, e.ResolutionNote, e.VerificationNote,
        e.VerificationRunId, e.ResolvedAt, e.VerifiedAt, e.ClosedAt, e.CreatedAt, e.UpdatedAt);

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
}
