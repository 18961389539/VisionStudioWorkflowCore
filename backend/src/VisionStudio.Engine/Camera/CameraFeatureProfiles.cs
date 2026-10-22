using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Camera;

public static class CameraFeatureProfiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Hash(CameraCommissioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var canonical = new
        {
            schemaVersion = profile.SchemaVersion,
            acquisition = profile.Acquisition?.Normalize(),
            profile.PacketSizeBytes,
            profile.InterPacketDelayTicks,
            profile.TriggerDelayUs,
            profile.TriggerInputLine,
            profile.LineDebouncerUs,
            profile.OutputLine,
            profile.OutputSource,
            profile.OutputInverted,
            profile.StrobeEnabled,
            profile.StrobeDelayUs,
            profile.StrobeDurationUs,
            profile.StrobePreDelayUs,
            profile.PtpEnabled,
            profile.ActionDeviceKey,
            profile.ActionGroupKey,
            profile.ActionGroupMask,
            profile.ActionSelector,
            advancedFeatures = (profile.AdvancedFeatures ?? new Dictionary<string, string>())
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static CameraCommissioningProfile Normalize(CameraCommissioningProfile profile)
    {
        if (profile.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported camera commissioning profile schema v{profile.SchemaVersion}; expected v1.");
        return profile with
        {
            Acquisition = profile.Acquisition?.Normalize(),
            PacketSizeBytes = profile.PacketSizeBytes is null ? null : Math.Clamp(profile.PacketSizeBytes.Value, 576, 16384),
            InterPacketDelayTicks = profile.InterPacketDelayTicks is null ? null : Math.Clamp(profile.InterPacketDelayTicks.Value, 0, 10_000_000),
            TriggerDelayUs = profile.TriggerDelayUs is null ? null : Math.Clamp(profile.TriggerDelayUs.Value, 0, 10_000_000),
            LineDebouncerUs = profile.LineDebouncerUs is null ? null : Math.Clamp(profile.LineDebouncerUs.Value, 0, 10_000_000),
            StrobeDelayUs = profile.StrobeDelayUs is null ? null : Math.Clamp(profile.StrobeDelayUs.Value, -10_000_000, 10_000_000),
            StrobeDurationUs = profile.StrobeDurationUs is null ? null : Math.Clamp(profile.StrobeDurationUs.Value, 0, 10_000_000),
            StrobePreDelayUs = profile.StrobePreDelayUs is null ? null : Math.Clamp(profile.StrobePreDelayUs.Value, 0, 10_000_000),
            TriggerInputLine = Clean(profile.TriggerInputLine),
            OutputLine = Clean(profile.OutputLine),
            OutputSource = Clean(profile.OutputSource),
            AdvancedFeatures = (profile.AdvancedFeatures ?? new Dictionary<string, string>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key.Trim(), x => x.Value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
