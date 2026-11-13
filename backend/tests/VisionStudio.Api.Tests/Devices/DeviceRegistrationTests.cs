using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VisionStudio.Engine.Device;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-10-13 R3 审查整改回归：R3-01（设备 ID 碰撞与持久配置丢失）、
/// R3-03（注册失败残留运行时状态/重试被拒）、R3-04（损坏配置静默跳过、健康误报）。
/// 每个用例对应审查报告的复现场景；另有成功路径验证（合法重试、两设备独立、修复后恢复）。
/// </summary>
public sealed class DeviceRegistrationTests : IDisposable
{
    private readonly string _lab = Path.Combine(Path.GetTempPath(), "vs-device-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_lab, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    private static object ModbusProfile(string id, int port = 502, string host = "127.0.0.1") => new
    {
        id,
        name = id,
        host,
        port,
        timeoutMs = 1000,
        tags = Array.Empty<object>()
    };

    // ---------- R3-01：ID 碰撞 ----------

    [Fact]
    public async Task Register_IdsThatSanitizeToSameFile_AreRejectedBeforeAnyStateChange()
    {
        // 复现：audit.a 与 audit_a 曾都返回 200、磁盘只剩 audit_a（后写覆盖先写），重启丢配置。
        // 现在：清洗会改变含义的 ID 在任何状态改变前被拒绝（400）；两者各自独立可用。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var profilesRoot = Path.Combine(factory.ContentRoot, "data", "devices");

        var dotted = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit.a"));
        Assert.Equal(HttpStatusCode.BadRequest, dotted.StatusCode);

        var plain = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit_a"));
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);

        var live = await client.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.DoesNotContain(live!, x => x.Id == "audit.a");           // 被拒绝者不得进入运行时列表
        Assert.Contains(live!, x => x.Id == "audit_a");
        var stored = await client.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles");
        Assert.Single(stored!, x => x.Id.StartsWith("audit"));
        Assert.True(File.Exists(Path.Combine(profilesRoot, "audit_a.json")));
    }

    [Fact]
    public async Task Register_UnicodeId_RoundTripsWithoutRewriting()
    {
        // 契约边界：清洗器原样保留的字符（含中文等 Unicode 字母）合法——文件名与 ID 一一对应；
        // 只拒绝会被清洗改写（如点号）的 ID，避免对现场既有中文设备名造成破坏性收紧。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("设备-一号"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(factory.ContentRoot, "data", "devices", "设备-一号.json")));
        var stored = await client.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles");
        Assert.Contains(stored!, x => x.Id == "设备-一号");
    }

    [Fact]
    public async Task SaveAndDelete_RefuseToTouchAFileThatBelongsToAnotherLegacyDevice()
    {
        // 遗留部署可能已存在 audit.a（文件 audit_a.json 内 Id=audit.a）。存储层必须核对文件身份：
        // 覆盖/删除都不得命中另一台设备的文件（审查确认的删除风险）。
        var root = Path.Combine(_lab, "devices");
        var store = new DeviceProfileStore(root);
        Directory.CreateDirectory(root);
        var legacy = new DeviceProfile("audit.a", "modbus-tcp", "legacy", "127.0.0.1", 502, 1000);
        File.WriteAllText(Path.Combine(root, "audit_a.json"), JsonSerializer.Serialize(legacy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        // 覆盖：新设备 audit_a 与遗留文件碰撞 → 拒绝，遗留文件原样保留。
        var incoming = new DeviceProfile("audit_a", "modbus-tcp", "new", "127.0.0.1", 502, 1000);
        var overwrite = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(incoming));
        Assert.Contains("collides with persisted device", overwrite.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audit.a", File.ReadAllText(Path.Combine(root, "audit_a.json")));

        // 删除：audit_a 不得删除属于 audit.a 的文件。
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync("audit_a"));
        Assert.Contains("collides with persisted device", delete.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(root, "audit_a.json")));

        // 遗留设备自身的更新与删除仍然可用（不遗失旧配置）。
        await store.SaveAsync(legacy with { Name = "legacy-updated" });
        Assert.True(await store.DeleteAsync("audit.a"));
        Assert.False(File.Exists(Path.Combine(root, "audit_a.json")));
    }

    [Fact]
    public async Task TwoDistinctDevices_SurviveSaveRestart_AndDeletingOneKeepsTheOther()
    {
        // 验收：合法 ID 各自独立保存；保存→重启→删除其中一个后，另一设备配置/端点/标签保持一致。
        var contentRoot = Path.Combine(_lab, "restart-root");
        using (var factory = new StorageVisionStudioApiFactory(contentRoot))
        {
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/devices/register/modbus-tcp",
                new { id = "audit-one", name = "one", host = "127.0.0.1", port = 1502, timeoutMs = 1000, unitId = 7, tags = Array.Empty<object>() })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/devices/register/modbus-tcp",
                new { id = "audit-two", name = "two", host = "127.0.0.1", port = 1503, timeoutMs = 1000, unitId = 9, tags = Array.Empty<object>() })).StatusCode);
        }

        // "重启"：同一内容根的新宿主（此前宿主已停止）从磁盘恢复两台设备（端点/单元号一致）。
        using var restarted = new StorageVisionStudioApiFactory(contentRoot);
        using var client2 = restarted.CreateClient();
        var restored = await client2.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles");
        Assert.NotNull(restored);
        Assert.Equal(1502, restored.Single(x => x.Id == "audit-one").Port);
        Assert.Equal(1503, restored.Single(x => x.Id == "audit-two").Port);
        var live = await client2.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.NotNull(live);
        Assert.Contains(live, x => x.Id == "audit-one");
        Assert.Contains(live, x => x.Id == "audit-two");

        // 删除其一：另一台配置不受影响。
        Assert.Equal(HttpStatusCode.NoContent, (await client2.DeleteAsync("/api/devices/audit-one")).StatusCode);
        var remaining = await client2.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles");
        Assert.Equal("audit-two", Assert.Single(remaining!, x => x.Id.StartsWith("audit")).Id);
    }

    // ---------- R3-03：注册失败不得残留运行时状态 ----------

    [Fact]
    public async Task FailedRegistration_LeavesNoRuntimeState_AndValidRetrySucceeds()
    {
        // 复现：port=0 注册返回 409，设备却留在运行时列表且磁盘无配置；用合法端口重试报 already registered。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var bad = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit-bad", port: 0));
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        var live = await client.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.DoesNotContain(live!, x => x.Id == "audit-bad");                       // 失败不得改变运行时状态
        var stored = await client.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles");
        Assert.DoesNotContain(stored!, x => x.Id == "audit-bad");

        // 修正参数重试：直接成功（旧实现此处 409 already registered）。
        var retry = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit-bad", port: 502));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var afterRetry = await client.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.NotNull(afterRetry);
        Assert.Contains(afterRetry, x => x.Id == "audit-bad");
    }

    [Fact]
    public async Task PersistenceFailureDuringRegister_RollsBackTheRuntimeRegistration()
    {
        // R3-03 的另一面：内存注册后持久化失败（写盘拒绝/身份碰撞）必须补偿回滚。
        // 构造：旧版遗留文件 audit_b.json 内 Id=audit.b（旧版把点号清洗为下划线），
        // 新注册合法 ID=audit_b → 身份碰撞 → 409，且 audit_b 不得留在运行时列表。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var profilesRoot = Path.Combine(factory.ContentRoot, "data", "devices");
        Directory.CreateDirectory(profilesRoot);
        var legacy = new DeviceProfile("audit.b", "modbus-tcp", "legacy", "127.0.0.1", 502, 1000);
        File.WriteAllText(Path.Combine(profilesRoot, "audit_b.json"), JsonSerializer.Serialize(legacy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var response = await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit_b"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var live = await client.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.DoesNotContain(live!, x => x.Id == "audit_b");                          // 补偿回滚生效
        Assert.Contains("audit.b", File.ReadAllText(Path.Combine(profilesRoot, "audit_b.json")));
    }

    // ---------- R3-04：损坏配置可见性 ----------

    [Fact]
    public async Task CorruptDeviceProfile_DegradesHealthWithLocatableReason()
    {
        // 复现：把设备文件改成 "{broken" 重启后，设备消失而 health 仍 ok/ready。
        var contentRoot = Path.Combine(_lab, "corrupt-root");
        var profilesRoot = Path.Combine(contentRoot, "data", "devices");
        using (var factory = new StorageVisionStudioApiFactory(contentRoot))
        {
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/devices/register/modbus-tcp", ModbusProfile("audit-corrupt"))).StatusCode);
            File.WriteAllText(Path.Combine(profilesRoot, "audit-corrupt.json"), "{broken");
        }

        using var restarted = new StorageVisionStudioApiFactory(contentRoot);
        using var client2 = restarted.CreateClient();
        var health = await client2.GetFromJsonAsync<HealthShape>("/api/health");
        Assert.Equal("degraded", health!.Status);                                      // 配置完整性降级，不再谎报 ok
        Assert.False(health.Ready);
        Assert.Contains(health.DegradedReasons!, r => r.Contains("device profile integrity", StringComparison.OrdinalIgnoreCase)
            && r.Contains("audit-corrupt.json", StringComparison.OrdinalIgnoreCase));
        var live = await client2.GetFromJsonAsync<List<DeviceShape>>("/api/devices");
        Assert.DoesNotContain(live!, x => x.Id == "audit-corrupt");
        // 原文件保留供修复。
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(profilesRoot, "audit-corrupt.json")));

        // 修复后（下一次扫描刷新）状态解除。
        File.WriteAllText(Path.Combine(profilesRoot, "audit-corrupt.json"), JsonSerializer.Serialize(
            new DeviceProfile("audit-corrupt", "modbus-tcp", "fixed", "127.0.0.1", 502, 1000),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        _ = await client2.GetFromJsonAsync<List<DeviceShape>>("/api/device-profiles"); // 触发重新扫描
        var healed = await client2.GetFromJsonAsync<HealthShape>("/api/health");
        Assert.Equal("ok", healed!.Status);
        Assert.True(healed.Ready);
    }

    [Fact]
    public async Task StoreReportsUnreadableProfilesWithoutLosingThem()
    {
        var root = Path.Combine(_lab, "problems");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ok.json"), JsonSerializer.Serialize(
            new DeviceProfile("ok", "modbus-tcp", "ok", "127.0.0.1", 502, 1000),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(root, "broken.json"), "{broken");
        File.WriteAllText(Path.Combine(root, "empty.json"), "null");
        var store = new DeviceProfileStore(root);

        var loaded = await store.ListAsync();

        Assert.Single(loaded);
        Assert.Equal(2, store.Problems.Count);
        Assert.Contains(store.Problems, p => p.FileName == "broken.json" && p.Error.Length > 0);
        Assert.Contains(store.Problems, p => p.FileName == "empty.json");
        Assert.True(File.Exists(Path.Combine(root, "broken.json")));   // 保留原文件
    }

    private sealed record DeviceShape(string Id, string Name, string Host, int Port);
    private sealed record HealthShape(string Status, bool Ready, IReadOnlyList<string>? DegradedReasons);
}
