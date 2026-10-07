using System.Globalization;
using System.Reflection;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Basler;

public sealed partial class BaslerPylonCameraDevice
{
    private static readonly HashSet<string> ProfileFeatureAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GevSCPSPacketSize", "GevSCPD", "TriggerDelay", "TriggerDelayAbs",
        "LineSelector", "LineMode", "LineSource", "LineInverter", "LineDebouncerTime", "LineDebouncerTimeAbs",
        "PtpEnable", "GevIEEE1588", "ActionDeviceKey", "ActionGroupKey", "ActionGroupMask", "ActionSelector"
    };

    private CameraCommissioningProfile? _lastCommissioningProfile;
    private string? _commissioningProfileHash;

    public CameraCommissioningCapabilities CommissioningCapabilities => new(
        FeatureBrowser: true,
        GigENetworkTuning: true,
        TriggerDelay: true,
        LineDebouncer: true,
        StrobeOutput: true,
        Ptp: true,
        ActionCommand: true,
        InputLines: ["Line1", "Line2", "Line3", "Line4"],
        OutputLines: ["Line1", "Line2", "Line3", "Line4"],
        OutputSources: ["ExposureActive", "FlashWindow", "FrameTriggerWait", "UserOutput1", "UserOutput2", "Low", "High"]);

    public string? CommissioningProfileHash => _commissioningProfileHash;

    public async Task<IReadOnlyList<CameraFeatureDescriptor>> ListFeaturesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireFeatureCamera();
            var definitions = new (string Key, string Name, string Category, CameraFeatureValueKind Kind, string? Unit, string[]? Options, bool Eligible)[]
            {
                ("ExposureTime", "Exposure Time", "Acquisition", CameraFeatureValueKind.Float, "µs", null, false),
                ("Gain", "Gain", "Acquisition", CameraFeatureValueKind.Float, "dB", null, false),
                ("AcquisitionFrameRate", "Acquisition Frame Rate", "Acquisition", CameraFeatureValueKind.Float, "Hz", null, false),
                ("TriggerMode", "Trigger Mode", "Trigger", CameraFeatureValueKind.Enum, null, ["Off", "On"], false),
                ("TriggerSource", "Trigger Source", "Trigger", CameraFeatureValueKind.Enum, null, ["Software", "Line1", "Line2", "Line3", "Line4", "Action1"], false),
                ("TriggerDelay", "Trigger Delay", "Trigger", CameraFeatureValueKind.Float, "µs", null, true),
                ("TriggerDelayAbs", "Trigger Delay (Legacy)", "Trigger", CameraFeatureValueKind.Float, "µs", null, true),
                ("GevSCPSPacketSize", "Packet Size", "Network", CameraFeatureValueKind.Integer, "bytes", null, true),
                ("GevSCPD", "Inter-Packet Delay", "Network", CameraFeatureValueKind.Integer, "ticks", null, true),
                ("LineSelector", "Line Selector", "Digital I/O", CameraFeatureValueKind.Enum, null, ["Line1", "Line2", "Line3", "Line4"], true),
                ("LineMode", "Line Mode", "Digital I/O", CameraFeatureValueKind.Enum, null, ["Input", "Output"], true),
                ("LineSource", "Line Source", "Digital I/O", CameraFeatureValueKind.Enum, null, ["ExposureActive", "FlashWindow", "FrameTriggerWait", "UserOutput1", "UserOutput2", "Low", "High"], true),
                ("LineInverter", "Line Inverter", "Digital I/O", CameraFeatureValueKind.Boolean, null, null, true),
                ("LineDebouncerTime", "Line Debouncer", "Digital I/O", CameraFeatureValueKind.Float, "µs", null, true),
                ("LineDebouncerTimeAbs", "Line Debouncer (Legacy)", "Digital I/O", CameraFeatureValueKind.Float, "µs", null, true),
                ("PtpEnable", "PTP Enable", "Synchronization", CameraFeatureValueKind.Boolean, null, null, true),
                ("GevIEEE1588", "IEEE 1588 / PTP", "Synchronization", CameraFeatureValueKind.Boolean, null, null, true),
                ("ActionDeviceKey", "Action Device Key", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionGroupKey", "Action Group Key", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionGroupMask", "Action Group Mask", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionSelector", "Action Selector", "Action Command", CameraFeatureValueKind.Integer, null, null, true)
            };

            return definitions
                .Where(x => HasParameter(x.Key))
                .Select(x => new CameraFeatureDescriptor(
                    x.Key, x.Name, x.Category, x.Kind, ReadText(x.Key), true, true, x.Eligible,
                    x.Unit, Options: x.Options, Description: Describe(x.Key)))
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<CameraCommissioningProfile> ReadCommissioningProfileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireFeatureCamera();
            var profile = ReadProfileCore(_lastCommissioningProfile);
            _lastCommissioningProfile = profile;
            _commissioningProfileHash = CameraFeatureProfiles.Hash(profile);
            return profile;
        }
        finally { _gate.Release(); }
    }

    public async Task<CameraCommissioningApplyResult> ApplyCommissioningProfileAsync(CameraCommissioningProfile profile, CancellationToken cancellationToken = default)
    {
        var normalized = CameraFeatureProfiles.Normalize(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireCommissioningWritable();
            var applied = new List<string>();
            var skipped = new List<string>();

            if (normalized.Acquisition is not null)
            {
                _settings = normalized.Acquisition.Normalize();
                ApplySettingsCore(_settings);
                applied.Add("Acquisition");
            }
            ApplyKnown("GevSCPSPacketSize", normalized.PacketSizeBytes, applied, skipped);
            ApplyKnown("GevSCPD", normalized.InterPacketDelayTicks, applied, skipped);
            ApplyAlias(["TriggerDelay", "TriggerDelayAbs"], normalized.TriggerDelayUs, "TriggerDelay", applied, skipped);

            if (!string.IsNullOrWhiteSpace(normalized.TriggerInputLine) && normalized.LineDebouncerUs is not null)
            {
                if (TrySet("LineSelector", normalized.TriggerInputLine!) &&
                    TrySetAlias(["LineDebouncerTime", "LineDebouncerTimeAbs"], normalized.LineDebouncerUs.Value))
                    applied.Add("LineDebouncer");
                else skipped.Add("LineDebouncer");
            }

            if (!string.IsNullOrWhiteSpace(normalized.OutputLine))
            {
                if (TrySet("LineSelector", normalized.OutputLine!))
                {
                    if (HasParameter("LineMode")) TrySet("LineMode", "Output");
                    if (!string.IsNullOrWhiteSpace(normalized.OutputSource))
                    {
                        if (TrySet("LineSource", normalized.OutputSource!)) applied.Add("LineSource"); else skipped.Add("LineSource");
                    }
                    if (normalized.OutputInverted is not null)
                    {
                        if (TrySet("LineInverter", normalized.OutputInverted.Value)) applied.Add("LineInverter"); else skipped.Add("LineInverter");
                    }
                }
                else skipped.Add("OutputLine");
            }

            ApplyAlias(["PtpEnable", "GevIEEE1588"], normalized.PtpEnabled, "PtpEnabled", applied, skipped);
            ApplyKnown("ActionDeviceKey", normalized.ActionDeviceKey, applied, skipped);
            ApplyKnown("ActionGroupKey", normalized.ActionGroupKey, applied, skipped);
            ApplyKnown("ActionGroupMask", normalized.ActionGroupMask, applied, skipped);
            ApplyKnown("ActionSelector", normalized.ActionSelector, applied, skipped);

            foreach (var feature in normalized.AdvancedFeatures ?? new Dictionary<string, string>())
            {
                if (!ProfileFeatureAllowList.Contains(feature.Key))
                    throw new InvalidOperationException($"Basler feature '{feature.Key}' is not profile-eligible.");
                if (TrySet(feature.Key, ParseValue(feature.Value))) applied.Add("Advanced:" + feature.Key); else skipped.Add("Advanced:" + feature.Key);
            }

            _lastCommissioningProfile = ReadProfileCore(normalized);
            _commissioningProfileHash = CameraFeatureProfiles.Hash(_lastCommissioningProfile);
            return new CameraCommissioningApplyResult(_lastCommissioningProfile, _commissioningProfileHash, applied, skipped);
        }
        finally { _gate.Release(); }
    }

    public async Task SetFeatureAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (!ProfileFeatureAllowList.Contains(key))
            throw new InvalidOperationException($"Feature '{key}' is read-only or outside the commissioning allowlist.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireCommissioningWritable();
            if (!TrySet(key, ParseValue(value))) throw new InvalidOperationException($"Basler feature '{key}' is unavailable or rejected value '{value}'.");
            var advanced = (_lastCommissioningProfile?.AdvancedFeatures ?? new Dictionary<string, string>())
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            advanced[key] = value;
            _lastCommissioningProfile = ReadProfileCore((_lastCommissioningProfile ?? new CameraCommissioningProfile(Acquisition: _settings)) with { AdvancedFeatures = advanced });
            _commissioningProfileHash = CameraFeatureProfiles.Hash(_lastCommissioningProfile);
        }
        finally { _gate.Release(); }
    }

    private CameraCommissioningProfile ReadProfileCore(CameraCommissioningProfile? hint)
    {
        var triggerLine = hint?.TriggerInputLine ?? _settings.ExternalTriggerSource;
        double? debouncer = null;
        if (!string.IsNullOrWhiteSpace(triggerLine) && TrySet("LineSelector", triggerLine!))
            debouncer = ReadDoubleAlias("LineDebouncerTime", "LineDebouncerTimeAbs");

        string? outputSource = null;
        bool? outputInverted = null;
        var outputLine = hint?.OutputLine;
        if (!string.IsNullOrWhiteSpace(outputLine) && TrySet("LineSelector", outputLine!))
        {
            outputSource = ReadText("LineSource");
            outputInverted = ReadBool("LineInverter");
        }

        return CameraFeatureProfiles.Normalize(new CameraCommissioningProfile(
            Acquisition: _settings,
            PacketSizeBytes: ReadInt("GevSCPSPacketSize"),
            InterPacketDelayTicks: ReadLong("GevSCPD"),
            TriggerDelayUs: ReadDoubleAlias("TriggerDelay", "TriggerDelayAbs"),
            TriggerInputLine: triggerLine,
            LineDebouncerUs: debouncer,
            OutputLine: outputLine,
            OutputSource: outputSource ?? hint?.OutputSource,
            OutputInverted: outputInverted ?? hint?.OutputInverted,
            StrobeEnabled: hint?.StrobeEnabled,
            PtpEnabled: ReadBoolAlias("PtpEnable", "GevIEEE1588"),
            ActionDeviceKey: ReadInt("ActionDeviceKey"),
            ActionGroupKey: ReadInt("ActionGroupKey"),
            ActionGroupMask: ReadInt("ActionGroupMask"),
            ActionSelector: ReadInt("ActionSelector"),
            AdvancedFeatures: hint?.AdvancedFeatures));
    }

    private void RequireFeatureCamera()
    {
        if (_camera is null || _state == CameraState.Closed) throw new InvalidOperationException($"Open Basler camera '{Id}' before reading commissioning features.");
    }

    private void RequireCommissioningWritable()
    {
        RequireFeatureCamera();
        if (_state == CameraState.Streaming) throw new InvalidOperationException($"Stop Basler camera '{Id}' before changing commissioning/network/I/O features.");
    }

    private object? Parameter(string key)
    {
        if (_camera is null) return null;
        var parameters = _camera.GetType().GetProperty("Parameters")?.GetValue(_camera);
        return parameters is null ? null : BaslerSdkReflection.Indexed(parameters, key);
    }

    private bool HasParameter(string key) => Parameter(key) is not null;
    private string? ReadText(string key) => BaslerSdkReflection.TryGetParameterText(_camera!, key);
    private double? ReadDouble(string key) => double.TryParse(ReadText(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
    private double? ReadDoubleAlias(params string[] keys) => keys.Select(ReadDouble).FirstOrDefault(x => x is not null);
    private int? ReadInt(string key) => int.TryParse(ReadText(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
    private long? ReadLong(string key) => long.TryParse(ReadText(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
    private bool? ReadBool(string key) => bool.TryParse(ReadText(key), out var x) ? x : ReadText(key) switch { "1" => true, "0" => false, _ => null };
    private bool? ReadBoolAlias(params string[] keys) => keys.Select(ReadBool).FirstOrDefault(x => x is not null);
    private bool TrySet(string key, object value) => HasParameter(key) && BaslerSdkReflection.TrySetParameter(_camera!, key, value);
    private bool TrySetAlias(IEnumerable<string> keys, object value) => keys.Any(key => TrySet(key, value));

    private void ApplyKnown(string key, object? value, List<string> applied, List<string> skipped)
    {
        if (value is null) return;
        if (TrySet(key, value)) applied.Add(key); else skipped.Add(key);
    }

    private void ApplyAlias(IEnumerable<string> keys, object? value, string label, List<string> applied, List<string> skipped)
    {
        if (value is null) return;
        if (TrySetAlias(keys, value)) applied.Add(label); else skipped.Add(label);
    }

    private static object ParseValue(string value)
    {
        if (bool.TryParse(value, out var b)) return b;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return value;
    }

    private static string Describe(string key) => key switch
    {
        "GevSCPSPacketSize" => "GigE packet size. Use the largest value supported end-to-end by NIC, switch and camera.",
        "GevSCPD" => "GigE inter-packet delay. Increase when multiple cameras share a link or driver drops occur.",
        "TriggerDelay" or "TriggerDelayAbs" => "Delay from accepted frame trigger to exposure start, in microseconds where supported.",
        "LineDebouncerTime" or "LineDebouncerTimeAbs" => "Reject input transitions shorter than the configured debounce time.",
        "LineSource" => "Signal driven on the selected output line. ExposureActive/FlashWindow are typical strobe sources.",
        "PtpEnable" or "GevIEEE1588" => "Enable IEEE 1588 / PTP clock synchronization on supported GigE models.",
        _ => "Vendor GenICam feature exposed through the commissioning allowlist."
    };
}
