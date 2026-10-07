using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

public abstract class CachedVendorProvenanceProbe(
    string kind,
    string assetId,
    string provider,
    bool requiredForProduction,
    TimeSpan cacheDuration) : IVendorHardwareProvenanceProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VendorProvenanceProbeResult? _cached;
    private DateTimeOffset _expiresAt;

    public string Kind { get; } = kind;
    public string AssetId { get; } = assetId;
    public string Provider { get; } = provider;
    public bool RequiredForProduction { get; } = requiredForProduction;

    public async ValueTask<VendorProvenanceProbeResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && now < _expiresAt) return _cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && now < _expiresAt) return _cached;
            try
            {
                var data = await CaptureCoreAsync(cancellationToken);
                _cached = new VendorProvenanceProbeResult(Provider, data, true, RequiredForProduction);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _cached = new VendorProvenanceProbeResult(
                    Provider,
                    new HardwareProvenanceData(Attributes: new Dictionary<string, string>
                    {
                        ["liveProbe"] = Provider,
                        ["liveProbeState"] = "unavailable"
                    }),
                    false,
                    RequiredForProduction,
                    ex.Message);
            }
            _expiresAt = DateTimeOffset.UtcNow + cacheDuration;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    protected abstract ValueTask<HardwareProvenanceData> CaptureCoreAsync(CancellationToken cancellationToken);

    public virtual void Dispose() => _gate.Dispose();
}
