using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api;

public sealed record GigEHostAddress(string Address, string? Mask);

public sealed record GigEHostNetworkInterface(
    string Id,
    string Name,
    string Description,
    string OperationalStatus,
    string InterfaceType,
    double SpeedMbps,
    int? MtuBytes,
    IReadOnlyList<GigEHostAddress> Ipv4Addresses,
    long? BytesReceived,
    long? BytesSent,
    long? IncomingPacketErrors,
    long? IncomingPacketDiscards,
    long? OutgoingPacketErrors,
    long? OutgoingPacketDiscards);

public sealed record GigECameraNetworkDiagnostic(
    string CameraId,
    string Driver,
    string? CameraIpAddress,
    string? NicId,
    string? NicName,
    double? NicSpeedMbps,
    int? NicMtuBytes,
    int? PacketSizeBytes,
    long? InterPacketDelayTicks,
    double? CurrentThroughputMbps,
    double? CurrentNicUtilization,
    bool NativeTransportTelemetry,
    string TransportSource,
    IReadOnlyList<string> Recommendations);

public sealed record GigENicLoadDiagnostic(
    string NicId,
    string NicName,
    double SpeedMbps,
    int? MtuBytes,
    IReadOnlyList<string> CameraIds,
    double CurrentCameraThroughputMbps,
    double CurrentUtilization,
    string Level);

public sealed record GigENetworkTrendPoint(
    string RunId,
    DateTimeOffset CompletedAt,
    int NativeCameraSamples,
    IReadOnlyDictionary<string, double> ThroughputByCameraMbps,
    double TotalThroughputMbps,
    long ReceivedFramesDelta,
    long LostFramesDelta,
    long FailedFramesDelta,
    long BufferUnderrunsDelta,
    long ReceivedPacketsDelta,
    long LostPacketsDelta,
    long FailedPacketsDelta,
    long ResendRequestsDelta,
    long ResentPacketsDelta,
    long ResynchronizationsDelta,
    double? FrameIssueRate,
    double? PacketIssueRate,
    double? ResendRequestRate);

public sealed record GigENetworkAssessment(
    string Status,
    bool Passed,
    int MinimumEvidenceRuns,
    int EvidenceRuns,
    double MaximumRecommendedNicUtilization,
    double MaximumFrameIssueRate,
    double MaximumPacketIssueRate,
    double MaximumResendRequestRate,
    double? PeakNicUtilization,
    double? AggregateFrameIssueRate,
    double? AggregatePacketIssueRate,
    double? AggregateResendRequestRate,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Recommendations);

public sealed record GigENetworkDiagnostics(
    string GroupId,
    string GroupName,
    string GroupConfigurationHash,
    DateTimeOffset CapturedAt,
    int HistoryRuns,
    IReadOnlyList<GigEHostNetworkInterface> HostInterfaces,
    IReadOnlyList<GigECameraNetworkDiagnostic> Cameras,
    IReadOnlyList<GigENicLoadDiagnostic> NicLoads,
    IReadOnlyList<GigENetworkTrendPoint> Trend,
    GigENetworkAssessment Assessment);

public sealed record GigENetworkCommissioningTestRequest(
    int Iterations = 100,
    bool Scheduled = false,
    int FrameTimeoutMs = 3000,
    int DelayMs = 50,
    int MinimumSamples = 20);

public sealed record GigENetworkCommissioningReport(
    string ReportSchemaVersion,
    string TestId,
    string GroupId,
    string Status,
    int RequestedIterations,
    int CompletedIterations,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    GigENetworkDiagnostics Diagnostics,
    string EvidenceHash,
    IReadOnlyList<string> Notes);

public sealed class GigENetworkDiagnosticsService(
    CameraSynchronizationService synchronization,
    CameraSynchronizationCommissioningService commissioning,
    CameraManager cameras,
    HardwareProvenanceService provenance)
{
    public async Task<GigENetworkDiagnostics> GetGroupAsync(string groupId, int historyLimit = 100, CancellationToken ct = default)
    {
        var group = await synchronization.GetAsync(groupId, ct);
        var history = await synchronization.ListRunsAsync(group.Id, Math.Clamp(historyLimit, 5, 500), ct);
        return await BuildAsync(group, history, Math.Min(20, Math.Clamp(historyLimit, 5, 500)), ct);
    }

    public async Task<CameraSynchronizationCommissioningTest> StartNetworkTestAsync(string groupId, GigENetworkCommissioningTestRequest request, CancellationToken ct = default)
    {
        var normalized = new CameraSynchronizationCommissioningTestRequest(
            Math.Clamp(request.Iterations, 5, 500), request.Scheduled,
            Math.Clamp(request.FrameTimeoutMs, 100, 30_000), Math.Clamp(request.DelayMs, 0, 10_000),
            Math.Clamp(request.MinimumSamples, 5, 500));
        return await commissioning.StartAsync(groupId, normalized, ct);
    }

    public async Task<GigENetworkCommissioningReport> GetNetworkTestReportAsync(string testId, CancellationToken ct = default)
    {
        var test = await commissioning.GetAsync(testId, ct);
        var group = await synchronization.GetAsync(test.GroupId, ct);
        var history = (await synchronization.ListRunsAsync(test.GroupId, 500, ct))
            .Where(x => string.Equals(x.Source, $"commissioning:{test.TestId}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.CompletedAt)
            .ToArray();
        var diagnostics = await BuildAsync(group, history, Math.Clamp(test.MinimumSamples, 5, 500), ct);
        var material = string.Join("|", new[]
        {
            test.TestId,
            test.GroupConfigurationHash,
            test.Status,
            diagnostics.Assessment.Status,
            diagnostics.Assessment.PeakNicUtilization?.ToString("R") ?? "",
            diagnostics.Assessment.AggregateFrameIssueRate?.ToString("R") ?? "",
            diagnostics.Assessment.AggregatePacketIssueRate?.ToString("R") ?? "",
            diagnostics.Assessment.AggregateResendRequestRate?.ToString("R") ?? ""
        }.Concat(diagnostics.Cameras.Select(x => $"{x.CameraId}:{x.CameraIpAddress}:{x.NicId}:{x.PacketSizeBytes}:{x.InterPacketDelayTicks}"))
         .Concat(diagnostics.Trend.Select(x => $"{x.RunId}:{x.TotalThroughputMbps:R}:{x.LostFramesDelta}:{x.LostPacketsDelta}:{x.ResendRequestsDelta}")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        var notes = new List<string>
        {
            "GigE report reuses the durable synchronization commissioning runner and camera_sync_runs evidence; it does not create a second measurement store.",
            "NIC utilization is based on vendor-reported camera throughput divided by the matched host adapter link speed. Other traffic on the same NIC is not included.",
            "Packet-size and inter-packet-delay recommendations are advisory; VisionStudio does not automatically change NIC, switch, or camera network settings."
        };
        return new GigENetworkCommissioningReport("v1", test.TestId, test.GroupId, test.Status, test.RequestedIterations,
            test.CompletedIterations, test.StartedAt, test.CompletedAt, diagnostics, hash, notes);
    }

    private async Task<GigENetworkDiagnostics> BuildAsync(
        CameraSynchronizationGroup group,
        IReadOnlyList<CameraSynchronizationRunRecord> history,
        int minimumEvidenceRuns,
        CancellationToken ct)
    {
        var host = CaptureHostNetworkInterfaces();
        var cameraRows = new List<GigECameraNetworkDiagnostic>();
        foreach (var cameraId in group.CameraIds)
        {
            var descriptor = cameras.Get(cameraId);
            string? ipAddress = null;
            string? transportLayer = null;
            try
            {
                var hw = await provenance.CaptureAsync("camera", cameraId, ct);
                if (hw.Data.Attributes is { Count: > 0 } attrs)
                {
                    if (attrs.TryGetValue("ipAddress", out var ip)) ipAddress = ip;
                    if (attrs.TryGetValue("transportLayer", out var tl)) transportLayer = tl;
                }
            }
            catch { /* Network diagnostics remain useful even when a provenance probe is temporarily unavailable. */ }

            CameraCommissioningProfile? profile = null;
            try { profile = await cameras.ReadCommissioningProfileAsync(cameraId, ct); } catch { }
            var telemetry = cameras.GetTransportTelemetry(cameraId);
            var matched = MatchInterface(ipAddress, host);
            var recommendations = BuildCameraRecommendations(
                cameraId, transportLayer, ipAddress, matched?.Snapshot,
                profile?.PacketSizeBytes, profile?.InterPacketDelayTicks,
                telemetry, group.CameraIds.Count);
            double? utilization = telemetry.ThroughputMbps is { } throughput && matched?.Snapshot.SpeedMbps is > 0
                ? throughput / matched.Snapshot.SpeedMbps
                : null;
            cameraRows.Add(new GigECameraNetworkDiagnostic(
                cameraId, descriptor.Driver, ipAddress, matched?.Snapshot.Id, matched?.Snapshot.Name,
                matched?.Snapshot.SpeedMbps, matched?.Snapshot.MtuBytes, profile?.PacketSizeBytes,
                profile?.InterPacketDelayTicks, telemetry.ThroughputMbps, utilization,
                telemetry.Native && telemetry.HasNativeCounters, telemetry.Source, recommendations));
        }

        var trend = BuildTransportTrend(history);
        var nicLoads = BuildNicLoads(host.Select(x => x.Snapshot).ToArray(), cameraRows);
        var assessment = Evaluate(group, cameraRows, host.Select(x => x.Snapshot).ToArray(), trend, minimumEvidenceRuns);
        return new GigENetworkDiagnostics(group.Id, group.Name, group.ConfigurationHash, DateTimeOffset.UtcNow,
            history.Count, host.Select(x => x.Snapshot).ToArray(), cameraRows, nicLoads, trend, assessment);
    }

    public static IReadOnlyList<GigENetworkTrendPoint> BuildTransportTrend(IReadOnlyList<CameraSynchronizationRunRecord> history)
    {
        var previous = new Dictionary<string, CameraTransportTelemetry>(StringComparer.OrdinalIgnoreCase);
        var output = new List<GigENetworkTrendPoint>();
        foreach (var run in history.OrderBy(x => x.CompletedAt))
        {
            var native = (run.TransportSnapshots ?? []).Where(x => x.Native && x.HasNativeCounters).ToArray();
            if (native.Length == 0) continue;
            long receivedFrames = 0, lostFrames = 0, failedFrames = 0, underruns = 0;
            long receivedPackets = 0, lostPackets = 0, failedPackets = 0, resendRequests = 0, resentPackets = 0, resync = 0;
            var throughputByCamera = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var current in native)
            {
                if (current.ThroughputMbps is { } throughput && double.IsFinite(throughput) && throughput >= 0)
                    throughputByCamera[current.CameraId] = throughput;
                if (previous.TryGetValue(current.CameraId, out var before))
                {
                    receivedFrames += Delta(before.ReceivedFrames, current.ReceivedFrames);
                    lostFrames += Delta(before.LostFrames, current.LostFrames);
                    failedFrames += Delta(before.FailedFrames, current.FailedFrames);
                    underruns += Delta(before.BufferUnderruns, current.BufferUnderruns);
                    receivedPackets += Delta(before.ReceivedPackets, current.ReceivedPackets);
                    lostPackets += Delta(before.LostPackets, current.LostPackets);
                    failedPackets += Delta(before.FailedPackets, current.FailedPackets);
                    resendRequests += Delta(before.ResendRequests, current.ResendRequests);
                    resentPackets += Delta(before.ResentPackets, current.ResentPackets);
                    resync += Delta(before.Resynchronizations, current.Resynchronizations);
                }
                previous[current.CameraId] = current;
            }
            var frameDenominator = receivedFrames + lostFrames;
            var packetDenominator = receivedPackets + lostPackets;
            output.Add(new GigENetworkTrendPoint(
                run.RunId, run.CompletedAt, native.Length, throughputByCamera,
                throughputByCamera.Values.Sum(), receivedFrames, lostFrames, failedFrames, underruns,
                receivedPackets, lostPackets, failedPackets, resendRequests, resentPackets, resync,
                frameDenominator <= 0 ? null : Math.Clamp((lostFrames + failedFrames) / (double)frameDenominator, 0, 1),
                packetDenominator <= 0 ? null : Math.Clamp((lostPackets + failedPackets) / (double)packetDenominator, 0, 1),
                packetDenominator <= 0 ? null : Math.Clamp(resendRequests / (double)packetDenominator, 0, 1)));
        }
        return output;
    }

    private static GigENetworkAssessment Evaluate(
        CameraSynchronizationGroup group,
        IReadOnlyList<GigECameraNetworkDiagnostic> cameras,
        IReadOnlyList<GigEHostNetworkInterface> nics,
        IReadOnlyList<GigENetworkTrendPoint> trend,
        int minimumEvidenceRuns)
    {
        const double maxRecommendedUtilization = 0.75;
        const double maxFrameIssueRate = 0.005;
        const double maxPacketIssueRate = 0.001;
        const double maxResendRequestRate = 0.01;
        var reasons = new List<string>();
        var recommendations = new List<string>();
        var evidence = trend.Count(x => x.NativeCameraSamples > 0);
        var missingNic = cameras.Where(x => string.IsNullOrWhiteSpace(x.CameraIpAddress) || string.IsNullOrWhiteSpace(x.NicId)).Select(x => x.CameraId).ToArray();
        var missingNative = cameras.Where(x => !x.NativeTransportTelemetry).Select(x => x.CameraId).ToArray();

        long receivedFrames = trend.Sum(x => x.ReceivedFramesDelta);
        long lostFrames = trend.Sum(x => x.LostFramesDelta);
        long failedFrames = trend.Sum(x => x.FailedFramesDelta);
        long receivedPackets = trend.Sum(x => x.ReceivedPacketsDelta);
        long lostPackets = trend.Sum(x => x.LostPacketsDelta);
        long failedPackets = trend.Sum(x => x.FailedPacketsDelta);
        long resendRequests = trend.Sum(x => x.ResendRequestsDelta);
        long underruns = trend.Sum(x => x.BufferUnderrunsDelta);
        long resync = trend.Sum(x => x.ResynchronizationsDelta);
        var frameDen = receivedFrames + lostFrames;
        var packetDen = receivedPackets + lostPackets;
        double? frameIssueRate = frameDen <= 0 ? null : Math.Clamp((lostFrames + failedFrames) / (double)frameDen, 0, 1);
        double? packetIssueRate = packetDen <= 0 ? null : Math.Clamp((lostPackets + failedPackets) / (double)packetDen, 0, 1);
        double? resendRate = packetDen <= 0 ? null : Math.Clamp(resendRequests / (double)packetDen, 0, 1);
        var peakUtilization = PeakNicUtilization(cameras, nics, trend);

        var hardFailure = false;
        if (frameIssueRate is { } fir && fir > maxFrameIssueRate) { hardFailure = true; reasons.Add($"Vendor frame loss/error rate {fir:P3} exceeds {maxFrameIssueRate:P3}."); }
        if (packetIssueRate is { } pir && pir > maxPacketIssueRate) { hardFailure = true; reasons.Add($"Vendor packet loss/error rate {pir:P3} exceeds {maxPacketIssueRate:P3}."); }
        if (resendRate is { } rr && rr > maxResendRequestRate) { hardFailure = true; reasons.Add($"Resend request rate {rr:P3} exceeds {maxResendRequestRate:P3}."); }
        if (underruns > 0) { hardFailure = true; reasons.Add($"Vendor telemetry recorded {underruns} buffer underrun(s)."); }
        if (resync > 0) { hardFailure = true; reasons.Add($"Vendor telemetry recorded {resync} stream resynchronization(s)."); }
        if (peakUtilization is { } util && util > 0.90) { hardFailure = true; reasons.Add($"Observed camera throughput reached {util:P1} of a matched NIC link, leaving too little headroom."); }
        if (peakUtilization is > maxRecommendedUtilization and <= 0.90)
            recommendations.Add($"Peak observed NIC utilization is {peakUtilization:P1}; keep sustained GigE Vision load below about {maxRecommendedUtilization:P0} when possible to leave resend/control headroom.");

        foreach (var camera in cameras)
        {
            foreach (var recommendation in camera.Recommendations) recommendations.Add($"{camera.CameraId}: {recommendation}");
            if (camera.NicMtuBytes is { } mtu && camera.PacketSizeBytes is { } packet && packet > mtu)
            {
                hardFailure = true;
                reasons.Add($"{camera.CameraId} packet size {packet} bytes exceeds matched NIC MTU {mtu} bytes.");
            }
        }

        string status;
        bool passed;
        if (hardFailure)
        {
            status = "Fail"; passed = false;
        }
        else if (evidence < minimumEvidenceRuns || missingNic.Length > 0 || missingNative.Length > 0)
        {
            status = "InsufficientData"; passed = false;
            if (evidence < minimumEvidenceRuns) reasons.Add($"Only {evidence}/{minimumEvidenceRuns} vendor-telemetry evidence runs are available.");
            if (missingNic.Length > 0) reasons.Add($"GigE IPv4 / host NIC mapping is unavailable for: {string.Join(", ", missingNic)}.");
            if (missingNative.Length > 0) reasons.Add($"Vendor-native transport counters are unavailable for: {string.Join(", ", missingNative)}.");
        }
        else
        {
            status = "Pass"; passed = true;
            reasons.Add($"{evidence} evidence runs satisfy the GigE transport acceptance thresholds for synchronization group '{group.Name}'.");
        }
        if (recommendations.Count == 0) recommendations.Add("No packet-size, pacing, or NIC-capacity tuning action is indicated by the current evidence.");
        return new GigENetworkAssessment(status, passed, minimumEvidenceRuns, evidence, maxRecommendedUtilization,
            maxFrameIssueRate, maxPacketIssueRate, maxResendRequestRate, peakUtilization,
            frameIssueRate, packetIssueRate, resendRate, reasons.Distinct().ToArray(), recommendations.Distinct().ToArray());
    }

    private static double? PeakNicUtilization(
        IReadOnlyList<GigECameraNetworkDiagnostic> cameras,
        IReadOnlyList<GigEHostNetworkInterface> nics,
        IReadOnlyList<GigENetworkTrendPoint> trend)
    {
        var cameraToNic = cameras.Where(x => x.NicId is not null).ToDictionary(x => x.CameraId, x => x.NicId!, StringComparer.OrdinalIgnoreCase);
        var speedByNic = nics.Where(x => x.SpeedMbps > 0).ToDictionary(x => x.Id, x => x.SpeedMbps, StringComparer.OrdinalIgnoreCase);
        double? peak = null;
        foreach (var point in trend)
        {
            var loads = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in point.ThroughputByCameraMbps)
            {
                if (!cameraToNic.TryGetValue(pair.Key, out var nicId) || !speedByNic.TryGetValue(nicId, out var speed) || speed <= 0) continue;
                loads[nicId] = loads.GetValueOrDefault(nicId) + pair.Value;
            }
            foreach (var pair in loads)
            {
                var utilization = pair.Value / speedByNic[pair.Key];
                peak = peak is null ? utilization : Math.Max(peak.Value, utilization);
            }
        }
        return peak;
    }

    private static IReadOnlyList<GigENicLoadDiagnostic> BuildNicLoads(
        IReadOnlyList<GigEHostNetworkInterface> nics,
        IReadOnlyList<GigECameraNetworkDiagnostic> cameras)
    {
        var list = new List<GigENicLoadDiagnostic>();
        foreach (var nic in nics)
        {
            var members = cameras.Where(x => string.Equals(x.NicId, nic.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (members.Length == 0) continue;
            var throughput = members.Sum(x => x.CurrentThroughputMbps ?? 0);
            var utilization = nic.SpeedMbps > 0 ? throughput / nic.SpeedMbps : 0;
            var level = utilization >= 0.90 ? "Critical" : utilization >= 0.75 ? "Warning" : "Healthy";
            list.Add(new GigENicLoadDiagnostic(nic.Id, nic.Name, nic.SpeedMbps, nic.MtuBytes,
                members.Select(x => x.CameraId).ToArray(), throughput, utilization, level));
        }
        return list.OrderByDescending(x => x.CurrentUtilization).ToArray();
    }

    private static IReadOnlyList<string> BuildCameraRecommendations(
        string cameraId,
        string? transportLayer,
        string? cameraIp,
        GigEHostNetworkInterface? nic,
        int? packetSize,
        long? interPacketDelay,
        CameraTransportTelemetry telemetry,
        int groupCameraCount)
    {
        var output = new List<string>();
        var isGigE = !string.IsNullOrWhiteSpace(cameraIp) || (transportLayer?.Contains("Gig", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isGigE)
        {
            output.Add("Camera does not expose a GigE IPv4 identity; network commissioning is not applicable or transport discovery is incomplete.");
            return output;
        }
        if (nic is null)
        {
            output.Add("No local IPv4 NIC subnet matches the camera address. Verify camera/NIC addressing, subnet mask, VLAN and route configuration.");
            return output;
        }
        if (!string.Equals(nic.OperationalStatus, OperationalStatus.Up.ToString(), StringComparison.OrdinalIgnoreCase))
            output.Add($"Matched NIC '{nic.Name}' is {nic.OperationalStatus}; it should be Up during commissioning.");
        if (nic.SpeedMbps is > 0 and < 1000)
            output.Add($"Matched NIC link speed is only {nic.SpeedMbps:F0} Mbit/s; GigE Vision production should normally use at least a dedicated 1 Gbit/s link.");
        var nicErrorOrDiscardCount = (nic.IncomingPacketErrors ?? 0) + (nic.IncomingPacketDiscards ?? 0) + (nic.OutgoingPacketErrors ?? 0) + (nic.OutgoingPacketDiscards ?? 0);
        if (nicErrorOrDiscardCount > 0)
            output.Add($"OS NIC error/discard counters are non-zero ({nicErrorOrDiscardCount}); these host-lifetime counters are advisory, so correlate them with vendor counter deltas during the commissioning window.");
        if (packetSize is null)
            output.Add("Packet Size is not readable from the camera profile; capture a commissioning profile before final network acceptance.");
        else if (nic.MtuBytes is { } mtu && packetSize > mtu)
            output.Add($"Camera packet size {packetSize} bytes exceeds NIC MTU {mtu}; align the camera, NIC and switch MTU end-to-end.");
        else if (nic.MtuBytes is >= 8000 && packetSize < 4000)
            output.Add($"NIC MTU is {nic.MtuBytes} bytes but camera packet size is {packetSize}; consider a larger packet size (commonly around 8 KiB) after validating every switch/NIC hop.");
        else if (nic.MtuBytes is <= 1500 && packetSize is > 1500)
            output.Add($"Camera packet size {packetSize} bytes requires jumbo frames but NIC MTU is {nic.MtuBytes}; reduce packet size or enable jumbo frames end-to-end.");
        if (groupCameraCount > 1 && (interPacketDelay ?? 0) == 0)
            output.Add("Multiple synchronized cameras share the group and Inter-Packet Delay is zero; add pacing when cameras share one NIC/switch uplink or when resend/loss counters increase.");
        if (telemetry.ResendRequests is > 0)
            output.Add("Vendor telemetry has recorded resend requests; check cable/switch quality first, then reduce burst pressure with packet pacing or separate NICs if needed.");
        if (telemetry.BufferUnderruns is > 0)
            output.Add("Buffer underruns were observed; verify host receive buffers/CPU scheduling and reduce aggregate camera bandwidth before accepting the network.");
        return output;
    }

    private sealed record HostInterfaceInfo(GigEHostNetworkInterface Snapshot, IReadOnlyList<(IPAddress Address, IPAddress? Mask)> Addresses);

    private static IReadOnlyList<HostInterfaceInfo> CaptureHostNetworkInterfaces()
    {
        var output = new List<HostInterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var props = nic.GetIPProperties();
                var addresses = new List<(IPAddress, IPAddress?)>();
                var publicAddresses = new List<GigEHostAddress>();
                foreach (var item in props.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    IPAddress? mask = null;
                    try { mask = item.IPv4Mask; } catch { }
                    addresses.Add((item.Address, mask));
                    publicAddresses.Add(new GigEHostAddress(item.Address.ToString(), mask?.ToString()));
                }
                if (publicAddresses.Count == 0) continue;
                int? mtu = null;
                try { mtu = props.GetIPv4Properties()?.Mtu; } catch { }
                double speed = 0;
                try { speed = Math.Max(0, nic.Speed / 1_000_000d); } catch { }
                long? received = null, sent = null, inErrors = null, inDiscards = null, outErrors = null, outDiscards = null;
                try
                {
                    var stats = nic.GetIPStatistics();
                    received = stats.BytesReceived; sent = stats.BytesSent;
                    inErrors = stats.IncomingPacketsWithErrors; inDiscards = stats.IncomingPacketsDiscarded;
                    outErrors = stats.OutgoingPacketsWithErrors; outDiscards = stats.OutgoingPacketsDiscarded;
                }
                catch { }
                var snapshot = new GigEHostNetworkInterface(nic.Id, nic.Name, nic.Description, nic.OperationalStatus.ToString(), nic.NetworkInterfaceType.ToString(),
                    speed, mtu, publicAddresses, received, sent, inErrors, inDiscards, outErrors, outDiscards);
                output.Add(new HostInterfaceInfo(snapshot, addresses));
            }
            catch { }
        }
        return output.OrderByDescending(x => x.Snapshot.OperationalStatus == OperationalStatus.Up.ToString()).ThenByDescending(x => x.Snapshot.SpeedMbps).ToArray();
    }

    private static HostInterfaceInfo? MatchInterface(string? cameraIpAddress, IReadOnlyList<HostInterfaceInfo> interfaces)
    {
        if (!IPAddress.TryParse(cameraIpAddress, out var cameraIp) || cameraIp.AddressFamily != AddressFamily.InterNetwork) return null;
        return interfaces
            .Where(x => x.Addresses.Any(a => SameSubnet(cameraIp, a.Address, a.Mask)))
            .OrderByDescending(x => x.Snapshot.OperationalStatus == OperationalStatus.Up.ToString())
            .ThenByDescending(x => x.Snapshot.SpeedMbps)
            .FirstOrDefault();
    }

    public static bool SameSubnet(IPAddress camera, IPAddress local, IPAddress? mask)
    {
        if (camera.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = camera.GetAddressBytes(); var b = local.GetAddressBytes();
        var m = mask?.GetAddressBytes();
        if (m is null || m.Length != 4)
            return a[0] == b[0] && a[1] == b[1] && a[2] == b[2]; // conservative fallback when an OS does not expose IPv4Mask
        for (var i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
    }

    private static long Delta(long? previous, long? current)
    {
        if (previous is null || current is null) return 0;
        return current.Value >= previous.Value ? current.Value - previous.Value : Math.Max(0, current.Value);
    }
}
