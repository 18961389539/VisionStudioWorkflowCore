using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api;

public sealed record CameraSynchronizationCommissioningTestRequest(
    int Iterations = 100,
    bool Scheduled = false,
    int FrameTimeoutMs = 3000,
    int DelayMs = 50,
    int MinimumSamples = 20);

public sealed record CameraSynchronizationCommissioningCameraEvidence(
    string CameraId, string Provider, HardwareProvenanceData Hardware, string HardwareFingerprint,
    string Completeness, bool LiveProbeConfigured, bool LiveProbeSucceeded, string? LiveProbeError,
    string PtpState, bool PtpSupported, bool PtpEnabled, long? OffsetFromMasterNs, string? MasterClockId,
    string? CommissioningProfileHash);

public sealed record CameraSynchronizationCommissioningTrendPoint(
    int Index, string RunId, DateTimeOffset CompletedAt, string Outcome, double? TriggerSkewUs, bool? WithinTolerance, string TimestampBasis);

public sealed record CameraSynchronizationCommissioningFrameEvidence(
    string CameraId, long Sequence, DateTimeOffset HostTimestamp, long? DeviceTimestampNs, long? TriggerId, double DeltaFromFirstUs);

public sealed record CameraSynchronizationCommissioningWorstRun(
    string RunId, DateTimeOffset CompletedAt, string Outcome, double? TriggerSkewUs, bool? WithinTolerance, string TimestampBasis, string? Error,
    IReadOnlyList<CameraSynchronizationCommissioningFrameEvidence> Frames);

public sealed record CameraSynchronizationCommissioningSignoff(
    string PreparedBy = "", string ReviewedBy = "", string ApprovedBy = "", string Notes = "", DateTimeOffset? SignedAt = null);

public sealed record CameraSynchronizationCommissioningReport(
    string ReportSchemaVersion,
    string TestId,
    string GroupId,
    string GroupName,
    string GroupDriver,
    string GroupConfigurationHash,
    IReadOnlyList<string> CameraIds,
    bool Scheduled,
    int RequestedIterations,
    int CompletedIterations,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double MaxAllowedSkewUs,
    bool RequirePtpLocked,
    long MaxPtpOffsetNs,
    CameraSynchronizationStatistics Statistics,
    IReadOnlyList<CameraSynchronizationCommissioningCameraEvidence> Cameras,
    IReadOnlyList<CameraSynchronizationCommissioningTrendPoint> Trend,
    IReadOnlyList<CameraSynchronizationCommissioningWorstRun> WorstRuns,
    CameraSynchronizationCommissioningSignoff Signoff,
    string EvidenceHash,
    IReadOnlyList<string> Notes,
    CameraSynchronizationPtpDiagnostics? PtpDiagnostics = null);

public sealed record CameraSynchronizationCommissioningTest(
    string TestId,
    string GroupId,
    string GroupConfigurationHash,
    bool Scheduled,
    int RequestedIterations,
    int CompletedIterations,
    int FrameTimeoutMs,
    int DelayMs,
    int MinimumSamples,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    CameraSynchronizationCommissioningReport? Report);

public sealed class CameraSynchronizationCommissioningStore(SqliteMetadataDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task CreateAsync(CameraSynchronizationCommissioningTest test, CancellationToken ct = default)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
INSERT INTO camera_sync_commissioning_tests(test_id,group_id,group_config_hash,scheduled,requested_iterations,completed_iterations,frame_timeout_ms,delay_ms,minimum_samples,status,started_at,completed_at,error,report_json)
VALUES($id,$group,$hash,$scheduled,$requested,$completed,$timeout,$delay,$minimum,$status,$started,NULL,NULL,NULL);
""";
        Bind(cmd, test);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(CameraSynchronizationCommissioningTest test, CancellationToken ct = default)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
UPDATE camera_sync_commissioning_tests SET completed_iterations=$completed,status=$status,completed_at=$completedAt,error=$error,report_json=$report WHERE test_id=$id;
""";
        cmd.Parameters.AddWithValue("$id", test.TestId);
        cmd.Parameters.AddWithValue("$completed", test.CompletedIterations);
        cmd.Parameters.AddWithValue("$status", test.Status);
        cmd.Parameters.AddWithValue("$completedAt", test.CompletedAt is null ? DBNull.Value : test.CompletedAt.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$error", (object?)test.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$report", test.Report is null ? DBNull.Value : JsonSerializer.Serialize(test.Report, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CameraSynchronizationCommissioningTest> GetAsync(string testId, CancellationToken ct = default)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT test_id,group_id,group_config_hash,scheduled,requested_iterations,completed_iterations,frame_timeout_ms,delay_ms,minimum_samples,status,started_at,completed_at,error,report_json FROM camera_sync_commissioning_tests WHERE test_id=$id;";
        cmd.Parameters.AddWithValue("$id", testId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new KeyNotFoundException($"Camera synchronization commissioning test '{testId}' does not exist.");
        return Read(r);
    }

    public async Task<IReadOnlyList<CameraSynchronizationCommissioningTest>> ListAsync(string groupId, int limit = 20, CancellationToken ct = default)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT test_id,group_id,group_config_hash,scheduled,requested_iterations,completed_iterations,frame_timeout_ms,delay_ms,minimum_samples,status,started_at,completed_at,error,report_json FROM camera_sync_commissioning_tests WHERE group_id=$group ORDER BY started_at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CameraSynchronizationCommissioningTest>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static void Bind(SqliteCommand cmd, CameraSynchronizationCommissioningTest t)
    {
        cmd.Parameters.AddWithValue("$id", t.TestId); cmd.Parameters.AddWithValue("$group", t.GroupId); cmd.Parameters.AddWithValue("$hash", t.GroupConfigurationHash);
        cmd.Parameters.AddWithValue("$scheduled", t.Scheduled ? 1 : 0); cmd.Parameters.AddWithValue("$requested", t.RequestedIterations); cmd.Parameters.AddWithValue("$completed", t.CompletedIterations);
        cmd.Parameters.AddWithValue("$timeout", t.FrameTimeoutMs); cmd.Parameters.AddWithValue("$delay", t.DelayMs); cmd.Parameters.AddWithValue("$minimum", t.MinimumSamples);
        cmd.Parameters.AddWithValue("$status", t.Status); cmd.Parameters.AddWithValue("$started", t.StartedAt.ToString("O"));
    }

    private static CameraSynchronizationCommissioningTest Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetString(9),
        DateTimeOffset.Parse(r.GetString(10)), r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : JsonSerializer.Deserialize<CameraSynchronizationCommissioningReport>(r.GetString(13), Json));
}

public sealed class CameraSynchronizationCommissioningService(
    CameraSynchronizationCommissioningStore store,
    CameraSynchronizationService synchronization,
    HardwareProvenanceService provenance,
    VisionStudio.Engine.Camera.CameraManager cameras,
    IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeByGroup = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<CameraSynchronizationCommissioningTest>> ListAsync(string groupId, int limit = 20, CancellationToken ct = default) => store.ListAsync(groupId, limit, ct);
    public Task<CameraSynchronizationCommissioningTest> GetAsync(string testId, CancellationToken ct = default) => store.GetAsync(testId, ct);

    public async Task<CameraSynchronizationCommissioningTest> StartAsync(string groupId, CameraSynchronizationCommissioningTestRequest request, CancellationToken ct = default)
    {
        request = request with {
            Iterations = Math.Clamp(request.Iterations, 5, 500), FrameTimeoutMs = Math.Clamp(request.FrameTimeoutMs, 100, 30_000),
            DelayMs = Math.Clamp(request.DelayMs, 0, 10_000), MinimumSamples = Math.Clamp(request.MinimumSamples, 5, 500)
        };
        var group = await synchronization.GetAsync(groupId, ct);
        await synchronization.ValidateOrThrowAsync(group, request.Scheduled, ct);
        if (_activeByGroup.ContainsKey(group.Id)) throw new InvalidOperationException($"Synchronization group '{groupId}' already has a running commissioning test on this host.");
        var test = new CameraSynchronizationCommissioningTest(Guid.NewGuid().ToString("N"), group.Id, group.ConfigurationHash, request.Scheduled,
            request.Iterations, 0, request.FrameTimeoutMs, request.DelayMs, request.MinimumSamples, "Queued", DateTimeOffset.UtcNow, null, null, null);
        await store.CreateAsync(test, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        if (!_running.TryAdd(test.TestId, cts) || !_activeByGroup.TryAdd(group.Id, test.TestId)) { cts.Dispose(); throw new InvalidOperationException("Could not register commissioning test."); }
        _ = Task.Run(() => RunAsync(test, cts.Token), CancellationToken.None);
        return test;
    }

    public async Task<CameraSynchronizationCommissioningTest> CancelAsync(string testId, CancellationToken ct = default)
    {
        var test = await store.GetAsync(testId, ct);
        if (_running.TryGetValue(testId, out var cts)) cts.Cancel();
        return test;
    }

    public async Task<CameraSynchronizationCommissioningReport> GetReportAsync(string testId, CancellationToken ct = default)
    {
        var test = await store.GetAsync(testId, ct);
        if (test.Report is null) throw new InvalidOperationException($"Commissioning test '{testId}' has not produced a final report yet.");
        return test.Report;
    }

    public async Task<string> GetReportHtmlAsync(string testId, CancellationToken ct = default)
    {
        var r = await GetReportAsync(testId, ct);
        static string H(object? v) => WebUtility.HtmlEncode(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        static string F(double? v, string format = "F2") => v is null ? "—" : v.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        static string SvgPolyline(IReadOnlyList<CameraSynchronizationCommissioningTrendPoint> points, double limit)
        {
            const int width = 980, height = 260, left = 54, right = 18, top = 18, bottom = 40;
            var measured = points.Where(x => x.TriggerSkewUs is not null).ToArray();
            var maxY = Math.Max(limit, measured.Length == 0 ? limit : measured.Max(x => x.TriggerSkewUs!.Value));
            maxY = Math.Max(1, maxY * 1.12);
            double X(int i) => left + (width - left - right) * (points.Count <= 1 ? 0 : i / (double)(points.Count - 1));
            double Y(double v) => top + (height - top - bottom) * (1 - Math.Clamp(v / maxY, 0, 1));
            var b = new StringBuilder();
            b.Append($"<svg viewBox='0 0 {width} {height}' role='img' aria-label='Trigger skew trend'>");
            b.Append("<rect x='0' y='0' width='100%' height='100%' fill='white'/>");
            for (var g = 0; g <= 4; g++) { var value = maxY * g / 4d; var y = Y(value); b.Append($"<line x1='{left}' y1='{y:F1}' x2='{width-right}' y2='{y:F1}' stroke='#e5e7eb'/><text x='{left-8}' y='{y+4:F1}' text-anchor='end' font-size='10' fill='#667085'>{value:F0}</text>"); }
            var limitY = Y(limit); b.Append($"<line x1='{left}' y1='{limitY:F1}' x2='{width-right}' y2='{limitY:F1}' stroke='#b42318' stroke-width='1.5' stroke-dasharray='6 4'/><text x='{width-right}' y='{Math.Max(12,limitY-5):F1}' text-anchor='end' font-size='10' fill='#b42318'>limit {limit:F1} µs</text>");
            var poly = string.Join(" ", points.Select((x,i) => x.TriggerSkewUs is null ? null : $"{X(i):F1},{Y(x.TriggerSkewUs.Value):F1}").Where(x => x is not null));
            if (!string.IsNullOrWhiteSpace(poly)) b.Append($"<polyline fill='none' stroke='#175cd3' stroke-width='2' points='{poly}'/>");
            foreach (var (pt,i) in points.Select((x,i)=>(x,i))) if (pt.TriggerSkewUs is not null) { var bad = pt.WithinTolerance == false || !string.Equals(pt.Outcome,"Completed",StringComparison.OrdinalIgnoreCase); b.Append($"<circle cx='{X(i):F1}' cy='{Y(pt.TriggerSkewUs.Value):F1}' r='{(bad?4:2.5)}' fill='{(bad?"#b42318":"#175cd3")}'><title>#{pt.Index} {pt.TriggerSkewUs:F2} µs · {H(pt.Outcome)}</title></circle>"); }
            b.Append($"<line x1='{left}' y1='{height-bottom}' x2='{width-right}' y2='{height-bottom}' stroke='#98a2b3'/><text x='{left}' y='{height-12}' font-size='10' fill='#667085'>Run 1</text><text x='{width-right}' y='{height-12}' text-anchor='end' font-size='10' fill='#667085'>Run {points.Count}</text><text transform='translate(12 {height/2}) rotate(-90)' text-anchor='middle' font-size='11' fill='#475467'>Trigger skew (µs)</text></svg>");
            return b.ToString();
        }
        static string SvgPtpTrend(CameraSynchronizationPtpDiagnostics diagnostics, long maxAllowedNs)
        {
            const int width = 980, height = 260, left = 64, right = 18, top = 18, bottom = 40;
            var points = diagnostics.Trend;
            var values = points.Where(x => x.MaxAbsOffsetNs is not null).Select(x => (double)x.MaxAbsOffsetNs!.Value).ToArray();
            var maxY = Math.Max(1d, Math.Max(maxAllowedNs, values.DefaultIfEmpty(0).Max()) * 1.12);
            double X(int i) => left + (width-left-right) * (points.Count <= 1 ? 0 : i/(double)(points.Count-1));
            double Y(double v) => top + (height-top-bottom) * (1-Math.Clamp(v/maxY,0,1));
            var b = new StringBuilder(); b.Append($"<svg viewBox='0 0 {width} {height}' role='img' aria-label='PTP offset trend'>");
            b.Append("<rect width='100%' height='100%' fill='white'/>");
            for (var g=0;g<=4;g++){var value=maxY*g/4d;var y=Y(value);b.Append($"<line x1='{left}' y1='{y:F1}' x2='{width-right}' y2='{y:F1}' stroke='#e5e7eb'/><text x='{left-8}' y='{y+4:F1}' text-anchor='end' font-size='10' fill='#667085'>{value:F0}</text>");}
            var limitY=Y(maxAllowedNs);b.Append($"<line x1='{left}' y1='{limitY:F1}' x2='{width-right}' y2='{limitY:F1}' stroke='#b42318' stroke-width='1.5' stroke-dasharray='6 4'/><text x='{width-right}' y='{Math.Max(12,limitY-5):F1}' text-anchor='end' font-size='10' fill='#b42318'>limit {maxAllowedNs} ns</text>");
            var poly=string.Join(" ",points.Select((x,i)=>x.MaxAbsOffsetNs is null?null:$"{X(i):F1},{Y(x.MaxAbsOffsetNs.Value):F1}").Where(x=>x is not null));
            if(!string.IsNullOrWhiteSpace(poly))b.Append($"<polyline fill='none' stroke='#7f56d9' stroke-width='2' points='{poly}'/>");
            foreach(var (pt,i) in points.Select((x,i)=>(x,i))) if(pt.MaxAbsOffsetNs is not null){var bad=!pt.AllPtpReady||!pt.MasterClockConsistent||pt.MaxAbsOffsetNs>maxAllowedNs;b.Append($"<circle cx='{X(i):F1}' cy='{Y(pt.MaxAbsOffsetNs.Value):F1}' r='{(bad?4:2.5)}' fill='{(bad?"#b42318":"#7f56d9")}'><title>run {H(pt.RunId)} · |offset|max {pt.MaxAbsOffsetNs} ns · ready={pt.AllPtpReady}</title></circle>");}
            b.Append($"<line x1='{left}' y1='{height-bottom}' x2='{width-right}' y2='{height-bottom}' stroke='#98a2b3'/><text x='{left}' y='{height-12}' font-size='10' fill='#667085'>Run 1</text><text x='{width-right}' y='{height-12}' text-anchor='end' font-size='10' fill='#667085'>Run {points.Count}</text><text transform='translate(12 {height/2}) rotate(-90)' text-anchor='middle' font-size='11' fill='#475467'>Max |PTP offset| (ns)</text></svg>");
            return b.ToString();
        }
        static string SvgPtpOffsets(IReadOnlyList<CameraSynchronizationCommissioningCameraEvidence> cameras, long maxAllowedNs)
        {
            const int width=980, height=220, left=180, right=40, top=18, row=34;
            var rows = cameras.Take(5).ToArray();
            var maxAbs = Math.Max(1L, Math.Max(maxAllowedNs, rows.Select(x => Math.Abs(x.OffsetFromMasterNs ?? 0)).DefaultIfEmpty(0).Max()));
            var scale = (width-left-right)/(double)maxAbs;
            var b=new StringBuilder(); b.Append($"<svg viewBox='0 0 {width} {Math.Max(height,top+rows.Length*row+28)}' role='img' aria-label='PTP offset snapshot'>");
            b.Append("<rect width='100%' height='100%' fill='white'/>");
            for(var i=0;i<rows.Length;i++){var c=rows[i]; var y=top+i*row; var value=Math.Abs(c.OffsetFromMasterNs??0); var w=Math.Min(width-left-right,value*scale); var bad=value>maxAllowedNs || !string.Equals(c.PtpState,"Locked",StringComparison.OrdinalIgnoreCase); b.Append($"<text x='{left-8}' y='{y+17}' text-anchor='end' font-size='11' fill='#344054'>{H(c.CameraId)}</text><rect x='{left}' y='{y+5}' width='{width-left-right}' height='16' rx='3' fill='#f2f4f7'/><rect x='{left}' y='{y+5}' width='{w:F1}' height='16' rx='3' fill='{(bad?"#f97066":"#53b1fd")}'/><text x='{left+Math.Min(width-left-right-4,w+5):F1}' y='{y+17}' font-size='10' fill='#344054'>{H(c.OffsetFromMasterNs)} ns · {H(c.PtpState)}</text>");}
            b.Append($"<text x='{left}' y='{top+rows.Length*row+16}' font-size='10' fill='#667085'>Snapshot captured at report completion · allowed |offset| ≤ {maxAllowedNs} ns</text></svg>"); return b.ToString();
        }
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Camera Sync Commissioning Report</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:36px;color:#18202a;max-width:1180px}h1,h2{margin:.5em 0}.meta,.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px 18px}.card{border:1px solid #ccd4dd;border-radius:7px;padding:12px;margin:14px 0;break-inside:avoid}.chart{border:1px solid #e4e7ec;border-radius:7px;padding:8px;margin:12px 0;overflow:hidden}.chart svg{width:100%;height:auto}table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #d9e0e7;padding:6px;text-align:left;vertical-align:top}th{background:#f4f6f8}.pass{color:#147d3f}.fail{color:#b42318}.mono{font-family:Consolas,monospace;font-size:11px}.bad{background:#fff1f0}.detail{margin:4px 0 12px 18px}.muted{color:#667085;font-size:11px}@media(max-width:760px){.meta,.grid{grid-template-columns:repeat(2,1fr)}body{margin:16px}}@media print{button{display:none}body{margin:10mm;max-width:none}.chart{break-inside:avoid}}</style></head><body>");
        sb.Append($"<h1>Multi-Camera Synchronization Commissioning Report</h1><div class='meta'><div><b>Test</b><br>{H(r.TestId)}</div><div><b>Group</b><br>{H(r.GroupName)} ({H(r.GroupId)})</div><div><b>Driver</b><br>{H(r.GroupDriver)}</div><div><b>Mode</b><br>{(r.Scheduled ? "Scheduled" : "Immediate")}</div><div><b>Started</b><br>{H(r.StartedAt)}</div><div><b>Completed</b><br>{H(r.CompletedAt)}</div><div><b>Iterations</b><br>{r.CompletedIterations}/{r.RequestedIterations}</div><div><b>Acceptance</b><br><span class='{(r.Statistics.Commissioning.Passed ? "pass" : "fail")}'>{H(r.Statistics.Commissioning.Status)}</span></div></div>");
        sb.Append($"<div class='card'><b>Evidence SHA-256</b><div class='mono'>{H(r.EvidenceHash)}</div><b>Group Config SHA-256</b><div class='mono'>{H(r.GroupConfigurationHash)}</div><div class='muted'>Evidence fingerprint verifies report material consistency; it is not a cryptographic signature.</div></div>");
        sb.Append($"<h2>Statistics</h2><div class='grid'><div>Completion<br><b>{r.Statistics.CompletionRate:P2}</b></div><div>Tolerance pass<br><b>{r.Statistics.TolerancePassRate:P2}</b></div><div>p50 / p95<br><b>{F(r.Statistics.P50SkewUs)} / {F(r.Statistics.P95SkewUs)} µs</b></div><div>p99 / Max<br><b>{F(r.Statistics.P99SkewUs)} / {F(r.Statistics.MaxSkewUs)} µs</b></div><div>Allowed skew<br><b>{r.MaxAllowedSkewUs:F2} µs</b></div><div>PTP runs<br><b>{r.Statistics.DevicePtpRuns}</b></div><div>Host fallback<br><b>{r.Statistics.HostArrivalRuns}</b></div><div>Failed/Cancelled<br><b>{r.Statistics.FailedRuns}/{r.Statistics.CancelledRuns}</b></div></div>");
        sb.Append("<h2>Skew Trend</h2><div class='chart'>").Append(SvgPolyline(r.Trend, r.MaxAllowedSkewUs)).Append("</div>");
        if (r.PtpDiagnostics is not null)
        {
            sb.Append($"<h2>PTP Timing Diagnostics</h2><div class='grid'><div class='card'><b>Runs with PTP evidence</b><br>{r.PtpDiagnostics.RunsWithPtpEvidence}</div><div class='card'><b>PTP not ready</b><br>{r.PtpDiagnostics.PtpNotReadyRuns}</div><div class='card'><b>Master clock inconsistencies</b><br>{r.PtpDiagnostics.InconsistentMasterClockRuns}</div><div class='card'><b>Offset ↔ skew correlation</b><br>{F(r.PtpDiagnostics.OffsetSkewPearsonCorrelation,"F3")}</div></div>");
            sb.Append("<div class='chart'>").Append(SvgPtpTrend(r.PtpDiagnostics, r.MaxPtpOffsetNs)).Append("</div>");
            sb.Append("<table><tr><th>Camera</th><th>PTP ready</th><th>Avg |offset| ns</th><th>p95 |offset| ns</th><th>Max |offset| ns</th><th>Master changes</th><th>Master IDs</th></tr>");
            foreach(var c in r.PtpDiagnostics.Cameras) sb.Append($"<tr><td>{H(c.CameraId)}</td><td>{c.ReadySamples}/{c.Samples} ({c.ReadyRate:P1})</td><td>{F(c.AverageAbsOffsetNs)}</td><td>{F(c.P95AbsOffsetNs)}</td><td>{H(c.MaxAbsOffsetNs)}</td><td>{c.MasterClockChanges}</td><td class='mono'>{H(string.Join(", ",c.MasterClockIds))}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<h2>PTP Offset Snapshot at Report Completion</h2><div class='chart'>").Append(SvgPtpOffsets(r.Cameras, r.MaxPtpOffsetNs)).Append("</div>");
        sb.Append("<h2>Camera Evidence</h2><table><tr><th>Camera</th><th>Model</th><th>Serial</th><th>Firmware</th><th>SDK/Software</th><th>PTP</th><th>Offset ns</th><th>Hardware fingerprint</th></tr>");
        foreach (var c in r.Cameras) sb.Append($"<tr><td>{H(c.CameraId)}</td><td>{H(c.Hardware.Model ?? c.Hardware.ProductName)}</td><td>{H(c.Hardware.SerialNumber)}</td><td>{H(c.Hardware.FirmwareVersion)}</td><td>{H(c.Hardware.SoftwareVersion)}</td><td>{H(c.PtpState)}</td><td>{H(c.OffsetFromMasterNs)}</td><td class='mono'>{H(c.HardwareFingerprint)}</td></tr>");
        sb.Append("</table><h2>Worst Samples & Timestamp Drill-down</h2><table><tr><th>Run</th><th>Completed</th><th>Outcome</th><th>Skew µs</th><th>Basis</th><th>Error</th></tr>");
        foreach (var w in r.WorstRuns) { sb.Append($"<tr class='{(w.WithinTolerance==false?"bad":"")}'><td class='mono'>{H(w.RunId)}</td><td>{H(w.CompletedAt)}</td><td>{H(w.Outcome)}</td><td>{F(w.TriggerSkewUs)}</td><td>{H(w.TimestampBasis)}</td><td>{H(w.Error)}</td></tr>"); sb.Append("<tr><td colspan='6'><div class='detail'><table><tr><th>Camera</th><th>Sequence</th><th>Device timestamp ns</th><th>Host timestamp</th><th>Trigger ID</th><th>Δ from first µs</th></tr>"); foreach(var f in w.Frames.OrderBy(x=>x.DeltaFromFirstUs)) sb.Append($"<tr><td>{H(f.CameraId)}</td><td>{f.Sequence}</td><td class='mono'>{H(f.DeviceTimestampNs)}</td><td>{H(f.HostTimestamp)}</td><td>{H(f.TriggerId)}</td><td><b>{f.DeltaFromFirstUs:F2}</b></td></tr>"); sb.Append("</table></div></td></tr>"); }
        sb.Append("</table><h2>Acceptance Sign-off</h2><table><tr><th>Prepared by</th><th>Reviewed by</th><th>Approved by</th><th>Date</th></tr><tr style='height:55px'><td></td><td></td><td></td><td></td></tr></table>");
        sb.Append("<p><button onclick='window.print()'>Print / Save PDF</button></p></body></html>");
        return sb.ToString();
    }

    private async Task RunAsync(CameraSynchronizationCommissioningTest initial, CancellationToken ct)
    {
        var current = initial with { Status = "Running" };
        try
        {
            await store.UpdateAsync(current, CancellationToken.None);
            for (var i = 0; i < current.RequestedIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await synchronization.TriggerAsync(current.GroupId,
                        new CameraSynchronizationTriggerRequest(current.Scheduled, FrameTimeoutMs: current.FrameTimeoutMs), ct,
                        $"commissioning:{current.TestId}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* Failure is already persisted by synchronization service; continue endurance test. */ }
                current = current with { CompletedIterations = i + 1 };
                await store.UpdateAsync(current, CancellationToken.None);
                if (current.DelayMs > 0 && i + 1 < current.RequestedIterations) await Task.Delay(current.DelayMs, ct);
            }
            current = await FinishAsync(current, "Completed", null);
        }
        catch (OperationCanceledException)
        {
            current = await FinishAsync(current, "Cancelled", "Commissioning test cancelled.");
        }
        catch (Exception ex)
        {
            current = await FinishAsync(current, "Failed", ex.Message);
        }
        finally
        {
            _activeByGroup.TryRemove(current.GroupId, out _);
            if (_running.TryRemove(current.TestId, out var cts)) cts.Dispose();
        }
    }

    private async Task<CameraSynchronizationCommissioningTest> FinishAsync(CameraSynchronizationCommissioningTest current, string status, string? error)
    {
        var group = await synchronization.GetAsync(current.GroupId, CancellationToken.None);
        var history = (await synchronization.ListRunsAsync(current.GroupId, 500, CancellationToken.None))
            .Where(x => string.Equals(x.Source, $"commissioning:{current.TestId}", StringComparison.OrdinalIgnoreCase)).ToArray();
        var stats = CameraSynchronizationService.ComputeStatistics(group, history, current.RequestedIterations, current.MinimumSamples);
        var completedAt = DateTimeOffset.UtcNow;
        var notes = new List<string> {
            $"Group configuration hash: {current.GroupConfigurationHash}.",
            $"Mode: {(current.Scheduled ? "scheduled Action Command" : "immediate Action Command")}.",
            $"Executed {current.CompletedIterations} of {current.RequestedIterations} requested iterations."
        };
        if (!string.Equals(group.ConfigurationHash, current.GroupConfigurationHash, StringComparison.OrdinalIgnoreCase))
            notes.Add("WARNING: synchronization group configuration changed while/after the test; report statistics refer to the captured run set.");
        var cameraEvidence = new List<CameraSynchronizationCommissioningCameraEvidence>();
        foreach (var cameraId in group.CameraIds)
        {
            var hw = await provenance.CaptureAsync("camera", cameraId, CancellationToken.None);
            var ptp = await cameras.GetTimeSynchronizationStatusAsync(cameraId, CancellationToken.None);
            cameraEvidence.Add(new CameraSynchronizationCommissioningCameraEvidence(
                cameraId, hw.Provider, hw.Data, hw.Fingerprint, hw.Completeness, hw.LiveProbeConfigured, hw.LiveProbeSucceeded, hw.LiveProbeError,
                ptp.State.ToString(), ptp.PtpSupported, ptp.PtpEnabled, ptp.OffsetFromMasterNs, ptp.MasterClockId, cameras.GetCommissioningProfileHash(cameraId)));
        }
        var trend = history.OrderBy(x => x.CompletedAt).Select((x, i) => new CameraSynchronizationCommissioningTrendPoint(
            i + 1, x.RunId, x.CompletedAt, x.Outcome, x.TriggerSkewUs, x.WithinTolerance, x.TimestampBasis)).ToArray();
        static IReadOnlyList<CameraSynchronizationCommissioningFrameEvidence> FrameEvidence(CameraSynchronizationRunRecord run)
        {
            if (run.Frames.Count == 0) return [];
            var useDevice = string.Equals(run.TimestampBasis, "DevicePtp", StringComparison.OrdinalIgnoreCase) && run.Frames.All(x => x.DeviceTimestampNs is not null);
            var firstNs = useDevice ? run.Frames.Min(x => x.DeviceTimestampNs!.Value) : 0L;
            var firstHostTicks = run.Frames.Min(x => x.HostTimestamp.UtcTicks);
            return run.Frames.Select(x => new CameraSynchronizationCommissioningFrameEvidence(
                x.CameraId, x.Sequence, x.HostTimestamp, x.DeviceTimestampNs, x.TriggerId,
                useDevice ? (x.DeviceTimestampNs!.Value - firstNs) / 1000d : (x.HostTimestamp.UtcTicks - firstHostTicks) / 10d)).OrderBy(x => x.DeltaFromFirstUs).ToArray();
        }
        var ptpDiagnostics = CameraSynchronizationService.ComputePtpDiagnostics(group, history, history.Length);
        var worst = history.OrderByDescending(x => x.TriggerSkewUs ?? double.MinValue).ThenByDescending(x => x.CompletedAt).Take(10)
            .Select(x => new CameraSynchronizationCommissioningWorstRun(x.RunId, x.CompletedAt, x.Outcome, x.TriggerSkewUs, x.WithinTolerance, x.TimestampBasis, x.Error, FrameEvidence(x))).ToArray();
        var evidenceMaterial = string.Join("|", new[] { current.TestId, current.GroupConfigurationHash, stats.Commissioning.Status, stats.P99SkewUs?.ToString("R") ?? "", stats.MaxSkewUs?.ToString("R") ?? "" }
            .Concat(cameraEvidence.Select(x => x.CameraId + ":" + x.HardwareFingerprint)).Concat(trend.Select(x => x.RunId + ":" + (x.TriggerSkewUs?.ToString("R") ?? "") + ":" + x.Outcome))
            .Concat(ptpDiagnostics.Trend.Select(x => x.RunId + ":" + (x.MaxAbsOffsetNs?.ToString() ?? "") + ":" + x.AllPtpReady + ":" + (x.MasterClockId ?? ""))));
        var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceMaterial))).ToLowerInvariant();
        var report = new CameraSynchronizationCommissioningReport("v4", current.TestId, current.GroupId, group.Name, group.Driver, current.GroupConfigurationHash, group.CameraIds, current.Scheduled,
            current.RequestedIterations, current.CompletedIterations, current.StartedAt, completedAt, group.MaxTriggerSkewUs, group.RequirePtpLocked, group.MaxPtpOffsetNs, stats, cameraEvidence, trend, worst, new(), evidenceHash, notes, ptpDiagnostics);
        var finished = current with { Status = status, CompletedAt = completedAt, Error = error, Report = report };
        await store.UpdateAsync(finished, CancellationToken.None);
        return finished;
    }
}
