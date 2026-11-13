using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Tests;

public sealed class CameraFeatureProfileTests
{
    [Fact]
    public void CameraFeatureProfileHash_IsStableAcrossAdvancedFeatureOrdering()
    {
        var a = new CameraCommissioningProfile(
            PacketSizeBytes: 9000,
            TriggerDelayUs: 125,
            AdvancedFeatures: new Dictionary<string, string> { ["ActionGroupMask"] = "7", ["GevSCPD"] = "1000" });
        var b = new CameraCommissioningProfile(
            PacketSizeBytes: 9000,
            TriggerDelayUs: 125,
            AdvancedFeatures: new Dictionary<string, string> { ["GevSCPD"] = "1000", ["ActionGroupMask"] = "7" });

        Assert.Equal(CameraFeatureProfiles.Hash(a), CameraFeatureProfiles.Hash(b));
    }

    [Fact]
    public void CameraFeatureProfileNormalize_ClampsTransportAndTimingValues()
    {
        var normalized = CameraFeatureProfiles.Normalize(new CameraCommissioningProfile(
            PacketSizeBytes: 999999,
            InterPacketDelayTicks: -10,
            TriggerDelayUs: -5,
            LineDebouncerUs: -1,
            StrobeDurationUs: -2));

        Assert.Equal(16384, normalized.PacketSizeBytes);
        Assert.Equal(0, normalized.InterPacketDelayTicks);
        Assert.Equal(0, normalized.TriggerDelayUs);
        Assert.Equal(0, normalized.LineDebouncerUs);
        Assert.Equal(0, normalized.StrobeDurationUs);
    }

    [Fact]
    public void CameraFeatureProfileNormalize_RejectsUnknownSchema()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CameraFeatureProfiles.Normalize(new CameraCommissioningProfile(SchemaVersion: 99)));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
