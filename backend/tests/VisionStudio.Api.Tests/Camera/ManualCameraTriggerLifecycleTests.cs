using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api.Tests;

/// <summary>
/// R01/R02 回归（2026-10-11 独立复审反例转正）：
///   · R01：手动相机触发必须等**真实下发**返回才能闭合意图——"入队成功"不再被当成"下发完成"；
///   · R02：相机动作在途（派发许可未释放/停止未确认）时管理员核对必须被拒绝。
/// 探针复现（隔离假相机屏障）：http=200 driverCompleted=False intentCount=0 /
/// camera-reconcile http=200 driverCompleted=False unresolved=0——两条反例在本组用例中均反转为正确行为。
/// </summary>
public sealed class ManualCameraTriggerLifecycleTests
{
    [Fact]
    public async Task ManualTrigger_DoesNotCompleteIntent_BeforeDriverDispatchReturns()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var cameras = factory.Services.GetRequiredService<CameraManager>();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        var camera = new BlockingCamera();
        cameras.Register(camera);
        await cameras.StartAsync(camera.Id);

        // 真实驱动调用进入显式屏障：HTTP 请求不得在此期间返回成功。
        var trigger = client.PostAsync($"/api/cameras/{camera.Id}/trigger", null);
        await camera.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(trigger.IsCompleted);
        Assert.Single(store.Load()); // 意图仍然在册——驱动未返回，动作未确认

        camera.Release.TrySetResult();
        var response = await trigger.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(camera.Completed);
        Assert.Empty(store.Load()); // 下发确认成功后才闭合

        await cameras.StopAsync(camera.Id);
    }

    [Fact]
    public async Task ManualTrigger_DispatchFailure_KeepsUnknownIntentAndBlocks()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var cameras = factory.Services.GetRequiredService<CameraManager>();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        var camera = new BlockingCamera();
        cameras.Register(camera);
        await cameras.StartAsync(camera.Id);

        var trigger = client.PostAsync($"/api/cameras/{camera.Id}/trigger", null);
        await camera.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 驱动下发阶段抛出：HTTP 失败，意图保留为 Unknown（耐久记录不得消失）。
        camera.FailWith = new InvalidOperationException("driver fault inside ExecuteSoftwareTriggerAsync");
        camera.Release.TrySetResult();
        var response = await trigger.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(response.IsSuccessStatusCode);

        var intents = store.Load();
        Assert.Single(intents);
        Assert.Equal(ManualActionIntentState.Unknown, intents[0].State);

        // 未核对的 Unknown 阻断后续设备动作（同进程其余入口统一闸门）。
        var write = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "blocked" } });
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);

        await cameras.StopAsync(camera.Id);
    }

    [Fact]
    public async Task ManualTrigger_RejectedBeforeIssue_LeavesNoResidue()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var cameras = factory.Services.GetRequiredService<CameraManager>();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        var camera = new BlockingCamera();
        cameras.Register(camera);
        await cameras.StartAsync(camera.Id);
        await cameras.StopAsync(camera.Id); // 未在采集 → 未下发任何动作

        var response = await client.PostAsync($"/api/cameras/{camera.Id}/trigger", null);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(store.Load()); // CameraTriggerNotIssuedException => AbortBeforeIssue，无残留
    }

    [Fact]
    public async Task Reconciliation_IsRejected_WhileCameraDispatchIsStillInFlight()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var cameras = factory.Services.GetRequiredService<CameraManager>();
        var auth = factory.Services.GetRequiredService<DeviceActionAuthorizationService>();
        var camera = new BlockingCamera();
        cameras.Register(camera);
        await cameras.StartAsync(camera.Id);

        // 已知在途：真实驱动调用仍卡在软件触发方法内（派发许可未释放）。
        var trigger = client.PostAsync($"/api/cameras/{camera.Id}/trigger", null);
        await camera.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 独立模拟"失败动作遗留的相机 Unknown"——旧实现此时核对返回 200 且 Unknown 消失。
        using (var intent = auth.BeginManualIntent("camera.trigger.unknown", [], [], [camera.Id]))
            intent.MarkUnknown("trigger did not return; driver remains blocked in flight");
        var pending = auth.UnresolvedManualIntents().Single();
        var resolve = await client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            manualIntentId = pending.RunId,
            reason = "attempted while driver call is known to be blocked",
            evidence = "counterexample text - must be rejected",
            deviceIdentityEvidence = camera.Id
        });
        Assert.Equal(HttpStatusCode.Conflict, resolve.StatusCode); // R02：在途核对必须 409
        Assert.Single(auth.UnresolvedManualIntents()); // Unknown 不消失

        camera.Release.TrySetResult();
        var response = await trigger.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await cameras.StopAsync(camera.Id);
    }

    private sealed class BlockingCamera : ICameraDevice
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? FailWith { get; set; }
        public bool Completed { get; private set; }
        public string Id => "review-blocked-camera";
        public string Name => Id;
        public string Driver => "review-fake";
        public string? Source => null;
        public CameraState State { get; private set; }
        public long FramesCaptured => 0;
        public string? LastError => null;
        public CameraSettings Settings { get; private set; } = new(TriggerMode: CameraTriggerMode.Software);
        public CameraCapabilities Capabilities => new();
        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { State = CameraState.Closed; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken = default) { State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public async Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            if (FailWith is { } failure) throw failure;
            Completed = true;
        }
        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // 不产出测试帧：随 Stop 取消退出采集循环（OCE -> break），不污染状态。
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("no test frame expected");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
