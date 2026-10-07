using System.Globalization;
using System.Reflection;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Hikrobot;

public sealed partial class HikrobotMvsCameraDevice
{
    private static readonly HashSet<string> ProfileFeatureAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GevSCPSPacketSize", "GevSCPD", "TriggerDelay", "LineSelector", "LineMode", "LineSource", "LineInverter", "LineDebouncerTime",
        "StrobeEnable", "StrobeLineDelay", "StrobeLineDuration", "StrobeLinePreDelay", "GevIEEE1588",
        "ActionDeviceKey", "ActionGroupKey", "ActionGroupMask", "ActionSelector"
    };

    private CameraCommissioningProfile? _lastCommissioningProfile;
    private string? _commissioningProfileHash;

    public CameraCommissioningCapabilities CommissioningCapabilities => new(
        FeatureBrowser: true,
        GigENetworkTuning: _identity?.Transport == "GigE" || _identity is null,
        TriggerDelay: true,
        LineDebouncer: true,
        StrobeOutput: true,
        Ptp: _identity?.Transport == "GigE" || _identity is null,
        ActionCommand: _identity?.Transport == "GigE" || _identity is null,
        InputLines: ["Line0", "Line1", "Line2", "Line3"],
        OutputLines: ["Line0", "Line1", "Line2", "Line3"],
        OutputSources: ["ExposureActive", "Strobe", "UserOutput0", "UserOutput1", "Off"]);

    public string? CommissioningProfileHash => _commissioningProfileHash;

    public async Task<IReadOnlyList<CameraFeatureDescriptor>> ListFeaturesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireFeatureCamera();
            var defs = new (string Key, string Name, string Category, CameraFeatureValueKind Kind, string? Unit, string[]? Options, bool Eligible)[]
            {
                ("ExposureTime", "Exposure Time", "Acquisition", CameraFeatureValueKind.Float, "µs", null, false),
                ("Gain", "Gain", "Acquisition", CameraFeatureValueKind.Float, "dB", null, false),
                ("AcquisitionFrameRate", "Acquisition Frame Rate", "Acquisition", CameraFeatureValueKind.Float, "Hz", null, false),
                ("TriggerMode", "Trigger Mode", "Trigger", CameraFeatureValueKind.Enum, null, ["Off", "On"], false),
                ("TriggerSource", "Trigger Source", "Trigger", CameraFeatureValueKind.Enum, null, ["Software", "Line0", "Line1", "Line2", "Line3"], false),
                ("TriggerDelay", "Trigger Delay", "Trigger", CameraFeatureValueKind.Float, "µs", null, true),
                ("GevSCPSPacketSize", "Packet Size", "Network", CameraFeatureValueKind.Integer, "bytes", null, true),
                ("GevSCPD", "Inter-Packet Delay", "Network", CameraFeatureValueKind.Integer, "ticks", null, true),
                ("LineSelector", "Line Selector", "Digital I/O", CameraFeatureValueKind.Enum, null, ["Line0", "Line1", "Line2", "Line3"], true),
                ("LineMode", "Line Mode", "Digital I/O", CameraFeatureValueKind.Enum, null, ["Input", "Strobe"], true),
                ("LineSource", "Line Source", "Digital I/O", CameraFeatureValueKind.Enum, null, ["ExposureActive", "Strobe", "UserOutput0", "UserOutput1", "Off"], true),
                ("LineInverter", "Line Inverter", "Digital I/O", CameraFeatureValueKind.Boolean, null, null, true),
                ("LineDebouncerTime", "Line Debouncer", "Digital I/O", CameraFeatureValueKind.Integer, "µs", null, true),
                ("StrobeEnable", "Strobe Enable", "Strobe", CameraFeatureValueKind.Boolean, null, null, true),
                ("StrobeLineDelay", "Strobe Delay", "Strobe", CameraFeatureValueKind.Integer, "µs", null, true),
                ("StrobeLineDuration", "Strobe Duration", "Strobe", CameraFeatureValueKind.Integer, "µs", null, true),
                ("StrobeLinePreDelay", "Strobe Pre-Delay", "Strobe", CameraFeatureValueKind.Integer, "µs", null, true),
                ("GevIEEE1588", "IEEE 1588 / PTP", "Synchronization", CameraFeatureValueKind.Boolean, null, null, true),
                ("ActionDeviceKey", "Action Device Key", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionGroupKey", "Action Group Key", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionGroupMask", "Action Group Mask", "Action Command", CameraFeatureValueKind.Integer, null, null, true),
                ("ActionSelector", "Action Selector", "Action Command", CameraFeatureValueKind.Integer, null, null, true)
            };
            var output = new List<CameraFeatureDescriptor>();
            foreach (var x in defs)
            {
                var value = ReadFeatureText(x.Key, x.Kind);
                if (value is null) continue;
                output.Add(new CameraFeatureDescriptor(x.Key, x.Name, x.Category, x.Kind, value, true, true, x.Eligible, x.Unit, Options: x.Options, Description: Describe(x.Key)));
            }
            return output;
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
            ApplyInteger("GevSCPSPacketSize", normalized.PacketSizeBytes, applied, skipped);
            ApplyInteger("GevSCPD", normalized.InterPacketDelayTicks, applied, skipped);
            ApplyFloat("TriggerDelay", normalized.TriggerDelayUs, applied, skipped);

            if (!string.IsNullOrWhiteSpace(normalized.TriggerInputLine) && normalized.LineDebouncerUs is not null)
            {
                if (TrySetEnum("LineSelector", normalized.TriggerInputLine!) && TrySetInteger("LineDebouncerTime", (long)normalized.LineDebouncerUs.Value)) applied.Add("LineDebouncer");
                else skipped.Add("LineDebouncer");
            }

            if (!string.IsNullOrWhiteSpace(normalized.OutputLine))
            {
                if (TrySetEnum("LineSelector", normalized.OutputLine!))
                {
                    if (!string.IsNullOrWhiteSpace(normalized.OutputSource))
                    {
                        if (TrySetEnum("LineSource", normalized.OutputSource!)) applied.Add("LineSource"); else skipped.Add("LineSource");
                    }
                    if (normalized.OutputInverted is not null)
                    {
                        if (TrySetBool("LineInverter", normalized.OutputInverted.Value)) applied.Add("LineInverter"); else skipped.Add("LineInverter");
                    }
                    if (normalized.StrobeEnabled is not null)
                    {
                        if (TrySetBool("StrobeEnable", normalized.StrobeEnabled.Value)) applied.Add("StrobeEnable"); else skipped.Add("StrobeEnable");
                    }
                    ApplyInteger("StrobeLineDelay", normalized.StrobeDelayUs is null ? null : (long?)normalized.StrobeDelayUs.Value, applied, skipped);
                    ApplyInteger("StrobeLineDuration", normalized.StrobeDurationUs is null ? null : (long?)normalized.StrobeDurationUs.Value, applied, skipped);
                    ApplyInteger("StrobeLinePreDelay", normalized.StrobePreDelayUs is null ? null : (long?)normalized.StrobePreDelayUs.Value, applied, skipped);
                }
                else skipped.Add("OutputLine");
            }

            if (normalized.PtpEnabled is not null)
            {
                if (TrySetBool("GevIEEE1588", normalized.PtpEnabled.Value)) applied.Add("PtpEnabled"); else skipped.Add("PtpEnabled");
            }
            ApplyInteger("ActionDeviceKey", normalized.ActionDeviceKey, applied, skipped);
            ApplyInteger("ActionGroupKey", normalized.ActionGroupKey, applied, skipped);
            ApplyInteger("ActionGroupMask", normalized.ActionGroupMask, applied, skipped);
            ApplyInteger("ActionSelector", normalized.ActionSelector, applied, skipped);

            foreach (var feature in normalized.AdvancedFeatures ?? new Dictionary<string, string>())
            {
                if (!ProfileFeatureAllowList.Contains(feature.Key)) throw new InvalidOperationException($"Hikrobot feature '{feature.Key}' is not profile-eligible.");
                if (TrySetRaw(feature.Key, feature.Value)) applied.Add("Advanced:" + feature.Key); else skipped.Add("Advanced:" + feature.Key);
            }

            _lastCommissioningProfile = ReadProfileCore(normalized);
            _commissioningProfileHash = CameraFeatureProfiles.Hash(_lastCommissioningProfile);
            return new CameraCommissioningApplyResult(_lastCommissioningProfile, _commissioningProfileHash, applied, skipped);
        }
        finally { _gate.Release(); }
    }

    public async Task SetFeatureAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (!ProfileFeatureAllowList.Contains(key)) throw new InvalidOperationException($"Feature '{key}' is read-only or outside the commissioning allowlist.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireCommissioningWritable();
            if (!TrySetRaw(key, value)) throw new InvalidOperationException($"Hikrobot feature '{key}' is unavailable or rejected value '{value}'.");
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
        double? debounce = null;
        if (!string.IsNullOrWhiteSpace(triggerLine) && TrySetEnum("LineSelector", triggerLine!)) debounce = ReadInteger("LineDebouncerTime");
        var outputLine = hint?.OutputLine;
        bool? inverted = null, strobe = null;
        double? strobeDelay = null, strobeDuration = null, strobePreDelay = null;
        if (!string.IsNullOrWhiteSpace(outputLine) && TrySetEnum("LineSelector", outputLine!))
        {
            inverted = ReadBool("LineInverter"); strobe = ReadBool("StrobeEnable");
            strobeDelay = ReadInteger("StrobeLineDelay"); strobeDuration = ReadInteger("StrobeLineDuration"); strobePreDelay = ReadInteger("StrobeLinePreDelay");
        }
        return CameraFeatureProfiles.Normalize(new CameraCommissioningProfile(
            Acquisition: _settings,
            PacketSizeBytes: ToInt(ReadInteger("GevSCPSPacketSize")),
            InterPacketDelayTicks: ReadInteger("GevSCPD"),
            TriggerDelayUs: ReadFloat("TriggerDelay"),
            TriggerInputLine: triggerLine,
            LineDebouncerUs: debounce,
            OutputLine: outputLine,
            OutputSource: hint?.OutputSource,
            OutputInverted: inverted ?? hint?.OutputInverted,
            StrobeEnabled: strobe ?? hint?.StrobeEnabled,
            StrobeDelayUs: strobeDelay,
            StrobeDurationUs: strobeDuration,
            StrobePreDelayUs: strobePreDelay,
            PtpEnabled: ReadBool("GevIEEE1588"),
            ActionDeviceKey: ToInt(ReadInteger("ActionDeviceKey")),
            ActionGroupKey: ToInt(ReadInteger("ActionGroupKey")),
            ActionGroupMask: ToInt(ReadInteger("ActionGroupMask")),
            ActionSelector: ToInt(ReadInteger("ActionSelector")),
            AdvancedFeatures: hint?.AdvancedFeatures));
    }

    private void RequireFeatureCamera()
    {
        if (_camera is null || _state == CameraState.Closed) throw new InvalidOperationException($"Open Hikrobot camera '{Id}' before reading commissioning features.");
    }

    private void RequireCommissioningWritable()
    {
        RequireFeatureCamera();
        if (_state == CameraState.Streaming) throw new InvalidOperationException($"Stop Hikrobot camera '{Id}' before changing commissioning/network/I/O features.");
    }

    private string? ReadFeatureText(string key, CameraFeatureValueKind kind) => kind switch
    {
        CameraFeatureValueKind.Boolean => ReadBool(key)?.ToString(),
        CameraFeatureValueKind.Float => ReadFloat(key)?.ToString("0.###", CultureInfo.InvariantCulture),
        CameraFeatureValueKind.Integer => ReadInteger(key)?.ToString(CultureInfo.InvariantCulture),
        CameraFeatureValueKind.Enum => ReadEnumNumeric(key)?.ToString(CultureInfo.InvariantCulture),
        _ => TryReadStringNode(_camera!, key)
    };

    private long? ReadInteger(string key) => ReadHolderValue(key, ["MV_CC_GetIntValueEx_NET", "MV_CC_GetIntValue_NET"], ["nCurValue", "nCurrentValue"], Convert.ToInt64);
    private double? ReadFloat(string key) => ReadHolderValue(key, ["MV_CC_GetFloatValue_NET"], ["fCurValue", "fCurrentValue"], Convert.ToDouble);
    private bool? ReadBool(string key) => ReadHolderValue(key, ["MV_CC_GetBoolValue_NET"], ["bCurValue", "bCurrentValue"], Convert.ToBoolean);
    private long? ReadEnumNumeric(string key) => ReadHolderValue(key, ["MV_CC_GetEnumValue_NET"], ["nCurValue", "nCurrentValue"], Convert.ToInt64);

    private T? ReadHolderValue<T>(string key, IEnumerable<string> methodNames, IEnumerable<string> memberNames, Func<object, T> converter) where T : struct
    {
        if (_camera is null || _myCameraType is null) return null;
        foreach (var methodName in methodNames)
        {
            var method = _myCameraType.GetMethods().FirstOrDefault(x => x.Name == methodName && x.GetParameters().Length == 2);
            if (method is null) continue;
            var holderType = method.GetParameters()[1].ParameterType.GetElementType() ?? method.GetParameters()[1].ParameterType;
            var holder = Activator.CreateInstance(holderType)!;
            object?[] args = [key, holder];
            try
            {
                if (Convert.ToInt32(method.Invoke(_camera, args)) != 0) continue;
                holder = args[1] ?? holder;
                foreach (var name in memberNames)
                {
                    var raw = HikrobotSdkReflection.Member(holder, name);
                    if (raw is not null) return converter(raw);
                }
            }
            catch { }
        }
        return null;
    }

    private bool TrySetInteger(string key, long value)
        => TryInvoke(_camera!, "MV_CC_SetIntValueEx_NET", key, value)
        || (value >= 0 && value <= uint.MaxValue && TryInvoke(_camera!, "MV_CC_SetIntValue_NET", key, (uint)value));
    private bool TrySetFloat(string key, double value) => TryInvoke(_camera!, "MV_CC_SetFloatValue_NET", key, (float)value);
    private bool TrySetBool(string key, bool value) => TryInvoke(_camera!, "MV_CC_SetBoolValue_NET", key, value);
    private bool TrySetEnum(string key, string value) => TryInvoke(_camera!, "MV_CC_SetEnumValueByString_NET", key, value);

    private bool TrySetRaw(string key, string value)
    {
        if (key is "LineSelector" or "LineMode" or "LineSource") return TrySetEnum(key, value);
        if (key is "LineInverter" or "StrobeEnable" or "GevIEEE1588") return bool.TryParse(value, out var b) && TrySetBool(key, b);
        if (key is "TriggerDelay") return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && TrySetFloat(key, f);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && TrySetInteger(key, i);
    }

    private void ApplyInteger(string key, long? value, List<string> applied, List<string> skipped)
    {
        if (value is null) return;
        if (TrySetInteger(key, value.Value)) applied.Add(key); else skipped.Add(key);
    }
    private void ApplyInteger(string key, int? value, List<string> applied, List<string> skipped) => ApplyInteger(key, value is null ? null : (long?)value.Value, applied, skipped);
    private void ApplyFloat(string key, double? value, List<string> applied, List<string> skipped)
    {
        if (value is null) return;
        if (TrySetFloat(key, value.Value)) applied.Add(key); else skipped.Add(key);
    }
    private static int? ToInt(long? value)
    {
        if (value is null || value.Value < int.MinValue || value.Value > int.MaxValue) return null;
        return (int)value.Value;
    }

    private static string Describe(string key) => key switch
    {
        "GevSCPSPacketSize" => "GigE packet size. Tune together with NIC jumbo-frame configuration.",
        "GevSCPD" => "Inter-packet delay used to shape GigE bandwidth when multiple cameras share a link.",
        "TriggerDelay" => "Delay from accepted trigger to image acquisition response, in microseconds where supported.",
        "LineDebouncerTime" => "Digital input debounce time in microseconds.",
        "StrobeLineDelay" => "Delay between exposure/trigger event and strobe output.",
        "StrobeLineDuration" => "Strobe output pulse duration.",
        "StrobeLinePreDelay" => "Advance time for strobe output where supported.",
        "GevIEEE1588" => "IEEE 1588 / PTP synchronization on supported GigE cameras.",
        _ => "MVS/GenICam commissioning feature exposed through the profile allowlist."
    };
}
