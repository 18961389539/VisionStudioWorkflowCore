using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class ProductionFleetOptions
{
    public string? StationId { get; set; }
    public ProductionStationOptions[] Stations { get; set; } = [];

    internal bool IsValid() =>
        (StationId is null || IsStationId(StationId)) &&
        Stations.Length <= 32 &&
        Stations.All(station => station.IsValid()) &&
        Stations.Select(station => station.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Stations.Length;

    internal static bool IsStationId(string value)
        => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed class ProductionStationOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string? DeliveryToken { get; set; }
    public string? ExpectedJobId { get; set; }
    public int? ExpectedVersion { get; set; }
    public string? ExpectedWorkflowHash { get; set; }

    internal bool IsValid() =>
        ProductionFleetOptions.IsStationId(Id) && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80 &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        IsToken(Token) && (DeliveryToken is null || (DeliveryToken != Token && IsToken(DeliveryToken))) &&
        (ExpectedJobId is null
            ? ExpectedVersion is null && ExpectedWorkflowHash is null
            : ExpectedJobId.Length > 0 && ExpectedJobId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') &&
              ExpectedVersion > 0 && (ExpectedWorkflowHash is null || IsWorkflowHash(ExpectedWorkflowHash)));

    private static bool IsToken(string token) => token.StartsWith("vst_", StringComparison.Ordinal) && token.Length <= 256 &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsWorkflowHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed record StationJob(string Name, int? PublishedVersion, string? PublishedWorkflowHash);

public sealed record ProductionStationStatus(
    string StationId, ProductionRuntimeState State, string? JobId, StationJob? Job,
    int? LockedVersion, string? LockedWorkflowHash, long CycleCount, long OkCount, long NgCount,
    string? LastDisposition, DateTimeOffset? LastCycleAt, int ActiveAlarms, int CriticalAlarms, int PendingAlarms);

public sealed record StationVersionTarget(string JobId, int Version, string? WorkflowHash);

public enum StationConnection { Waiting, Online, Offline, Timeout, Unauthorized, InvalidResponse, IdentityMismatch }
public enum StationVersionCheck { NotConfigured, Unverified, Matches, PublishedOnly, Mismatch }

public sealed record ProductionStationOverview(
    string Id, string Name, string OperatorUrl, StationConnection Connection, DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSeenAt, ProductionStationStatus? Status, StationVersionTarget? Target, StationVersionCheck VersionCheck,
    bool RecipeDeliveryEnabled = false);

public sealed class ProductionFleet(IOptions<ProductionFleetOptions> options, IHttpClientFactory clients, TimeProvider clock)
    : BackgroundService
{
    internal const string HttpClientName = "production-station";
    internal const string StationPath = "api/production/stations/local";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions StationJson = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly ProductionStationOptions[] _stations = options.Value.Stations;
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.Ordinal);

    private sealed record Observation(StationConnection Connection, DateTimeOffset CheckedAt, DateTimeOffset? LastSeenAt,
        ProductionStationStatus? Status);

    public IReadOnlyList<ProductionStationOverview> Snapshot()
    {
        var now = clock.GetUtcNow();
        return _stations.Select(station =>
        {
            _observations.TryGetValue(station.Id, out var last);
            var connection = last?.Connection ?? StationConnection.Waiting;
            if (connection == StationConnection.Online && now - last!.CheckedAt > StaleAfter)
                connection = StationConnection.Offline;
            var target = station.ExpectedJobId is { } jobId && station.ExpectedVersion is { } version
                ? new StationVersionTarget(jobId, version, station.ExpectedWorkflowHash)
                : null;
            return new ProductionStationOverview(station.Id, station.Name, new Uri(BaseUri(station), "?view=operator").AbsoluteUri,
                connection, last?.CheckedAt, last?.LastSeenAt, last?.Status, target,
                CheckVersion(target, connection == StationConnection.Online ? last?.Status : null), station.DeliveryToken is not null);
        }).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_stations.Length == 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            await Task.Delay(PollInterval, clock, stoppingToken);
        }
    }

    internal Task PollAsync(CancellationToken ct) => Parallel.ForEachAsync(_stations,
        new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, PollStationAsync);

    private async ValueTask PollStationAsync(ProductionStationOptions station, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(station), StationPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", station.Token);
        var outcome = StationConnection.Offline;
        ProductionStationStatus? status = null;
        try
        {
            // ResponseContentRead applies the named client's body-size limit through the entire request.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            outcome = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StationConnection.Unauthorized,
                HttpStatusCode.NotFound => StationConnection.InvalidResponse,
                _ when response.IsSuccessStatusCode => StationConnection.InvalidResponse,
                _ => StationConnection.Offline
            };
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<ProductionStationStatus>(StationJson, timeout.Token);
                if (ValidStatus(payload))
                {
                    outcome = string.Equals(station.Id, payload!.StationId, StringComparison.Ordinal)
                        ? StationConnection.Online : StationConnection.IdentityMismatch;
                    if (outcome == StationConnection.Online) status = payload;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = StationConnection.Timeout; }
        catch (HttpRequestException) { outcome = StationConnection.Offline; }
        catch (JsonException) { outcome = StationConnection.InvalidResponse; }
        catch (NotSupportedException) { outcome = StationConnection.InvalidResponse; }

        ct.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        _observations.TryGetValue(station.Id, out var previous);
        _observations[station.Id] = status is null
            ? new Observation(outcome, now, previous?.LastSeenAt, previous?.Status)
            : new Observation(outcome, now, now, status);
    }

    internal static Uri BaseUri(ProductionStationOptions station) => new(station.BaseUrl.TrimEnd('/') + "/");

    private static bool ValidStatus(ProductionStationStatus? status)
        => status is not null && !string.IsNullOrEmpty(status.StationId) &&
           status.CycleCount >= 0 && status.OkCount >= 0 && status.NgCount >= 0 &&
           status.ActiveAlarms >= 0 && status.CriticalAlarms >= 0 && status.PendingAlarms >= 0 &&
           status.CriticalAlarms <= status.ActiveAlarms && status.PendingAlarms <= status.ActiveAlarms &&
           (status.LockedVersion is null || (status.LockedVersion > 0 && !string.IsNullOrEmpty(status.JobId))) &&
           (status.Job?.PublishedVersion is null || status.Job.PublishedVersion > 0);

    private static StationVersionCheck CheckVersion(StationVersionTarget? target, ProductionStationStatus? status)
    {
        if (target is null) return StationVersionCheck.NotConfigured;
        if (status is null) return StationVersionCheck.Unverified;
        if (!string.Equals(target.JobId, status.JobId, StringComparison.Ordinal)) return StationVersionCheck.Mismatch;

        bool Matches(int? version, string? workflowHash) => version == target.Version &&
            (target.WorkflowHash is null || string.Equals(target.WorkflowHash, workflowHash, StringComparison.OrdinalIgnoreCase));
        if (!Matches(status.Job?.PublishedVersion, status.Job?.PublishedWorkflowHash)) return StationVersionCheck.Mismatch;
        return status.LockedVersion is null ? StationVersionCheck.PublishedOnly
            : Matches(status.LockedVersion, status.LockedWorkflowHash) ? StationVersionCheck.Matches : StationVersionCheck.Mismatch;
    }
}
