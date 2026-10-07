using Microsoft.Data.Sqlite;
using VisionStudio.Api;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

public sealed class CameraFeatureProfileStoreTests
{
    [Fact]
    public async Task SchemaV5_CreatesCameraFeatureProfilesTable()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        Assert.Equal(16, db.CurrentSchemaVersion);

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_feature_profiles';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CameraFeatureProfileStore_RoundTripsPortableProfileAndHash()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraFeatureProfileStore(db);
        var profile = new CameraCommissioningProfile(
            Acquisition: new CameraSettings(ExposureUs: 3200, TriggerMode: CameraTriggerMode.External, ExternalTriggerSource: "Line1"),
            PacketSizeBytes: 9000,
            InterPacketDelayTicks: 1250,
            TriggerDelayUs: 80,
            OutputLine: "Line2",
            OutputSource: "ExposureActive",
            PtpEnabled: true);

        var saved = await store.UpsertAsync("top-camera", "Top Camera", "basler-pylon", profile, "camera-top");
        var loaded = await store.GetAsync("top-camera");

        Assert.Equal(saved.ProfileHash, loaded.ProfileHash);
        Assert.Equal(CameraFeatureProfiles.Hash(loaded.Profile), loaded.ProfileHash);
        Assert.Equal(9000, loaded.Profile.PacketSizeBytes);
        Assert.Equal("camera-top", loaded.SourceCameraId);
    }
    [Fact]
    public async Task CameraFeatureProfileStore_RejectsTamperedProfilePayload()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new CameraFeatureProfileStore(db);
        await store.UpsertAsync("tamper-test", "Tamper Test", "basler-pylon", new CameraCommissioningProfile(PacketSizeBytes: 1500));

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE camera_feature_profiles SET profile_json = replace(profile_json, '1500', '9000') WHERE id='tamper-test';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync("tamper-test"));
    }

}
