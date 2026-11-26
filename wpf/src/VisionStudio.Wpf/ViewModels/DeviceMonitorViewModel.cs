using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;

namespace VisionStudio.Wpf.ViewModels;

public sealed record DeviceTagDisplayItem(
    string TagId,
    string Name,
    string Address,
    DeviceTagDataType DataType,
    bool Writable,
    string ValueText,
    DeviceTagQuality Quality,
    DateTimeOffset? Timestamp,
    string? Unit);

/// <summary>
/// 设备与相机实时监视 ViewModel：
/// 直接读取进程内 <see cref="MonitorSnapshotReader"/>，并将采集到的 <c>Mat</c> 原地转为 <c>BitmapSource</c> 展示；
/// 支持相机连续预览、软触发、参数下发、设备标签读写与机器人控制。
/// </summary>
public sealed partial class DeviceMonitorViewModel : DesktopWorkspaceViewModel
{
    private readonly IVisionImageViewerAdapter _cameraViewer;
    private CancellationTokenSource? _livePreviewCts;
    private Task? _livePreviewTask;

    public DeviceMonitorViewModel(InProcessVisionHost host, IVisionImageViewerAdapter cameraViewer) : base(host)
    {
        _cameraViewer = cameraViewer;
        RefreshCommand = CreateCommand("刷新设备状态", RefreshAllAsync);
        OpenAndStartCameraCommand = CreateCommand("打开并采集相机", OpenAndStartCameraAsync, () => SelectedCamera is not null);
        StopAndCloseCameraCommand = CreateCommand("停止并关闭相机", StopAndCloseCameraAsync, () => SelectedCamera is not null);
        GrabLiveFrameCommand = CreateCommand("相机取图", GrabLiveFrameAsync, () => SelectedCamera is not null);
        TriggerAndGrabCommand = CreateCommand("软触发取图", TriggerAndGrabAsync, () => SelectedCamera is not null);
        ApplyCameraSettingsCommand = CreateCommand("应用相机参数", ApplyCameraSettingsAsync, () => SelectedCamera is not null);
        LoadCameraFeaturesCommand = CreateCommand("读取相机高级特性", LoadCameraFeaturesAsync, () => SelectedCamera is not null);

        ConnectDeviceCommand = CreateCommand("连接设备", ConnectDeviceAsync, () => SelectedDevice is not null);
        DisconnectDeviceCommand = CreateCommand("断开设备", DisconnectDeviceAsync, () => SelectedDevice is not null);
        RefreshDeviceTagsCommand = CreateCommand("读取设备标签", RefreshDeviceTagsAsync, () => SelectedDevice is not null);
        WriteDeviceTagCommand = CreateCommand("写入设备标签", WriteDeviceTagAsync,
            () => SelectedDevice is not null && SelectedDeviceTag is { Writable: true });

        ConnectRobotCommand = CreateCommand("连接机器人", ConnectRobotAsync, () => SelectedRobot is not null);
        DisconnectRobotCommand = CreateCommand("断开机器人", DisconnectRobotAsync, () => SelectedRobot is not null);
        StopRobotCommand = CreateCommand("停止机器人", StopRobotAsync, () => SelectedRobot is not null);
        ResetRobotFaultCommand = CreateCommand("复位机器人故障", ResetRobotFaultAsync, () => SelectedRobot is not null);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand OpenAndStartCameraCommand { get; }
    public IAsyncRelayCommand StopAndCloseCameraCommand { get; }
    public IAsyncRelayCommand GrabLiveFrameCommand { get; }
    public IAsyncRelayCommand TriggerAndGrabCommand { get; }
    public IAsyncRelayCommand ApplyCameraSettingsCommand { get; }
    public IAsyncRelayCommand LoadCameraFeaturesCommand { get; }

    public IAsyncRelayCommand ConnectDeviceCommand { get; }
    public IAsyncRelayCommand DisconnectDeviceCommand { get; }
    public IAsyncRelayCommand RefreshDeviceTagsCommand { get; }
    public IAsyncRelayCommand WriteDeviceTagCommand { get; }

    public IAsyncRelayCommand ConnectRobotCommand { get; }
    public IAsyncRelayCommand DisconnectRobotCommand { get; }
    public IAsyncRelayCommand StopRobotCommand { get; }
    public IAsyncRelayCommand ResetRobotFaultCommand { get; }

    public ObservableCollection<CameraDescriptor> Cameras { get; } = [];
    public ObservableCollection<DeviceDescriptor> Devices { get; } = [];
    public ObservableCollection<RobotDescriptor> Robots { get; } = [];
    public ObservableCollection<CameraFeatureDescriptor> CameraFeatures { get; } = [];
    public ObservableCollection<DeviceTagDisplayItem> DeviceTags { get; } = [];
    public IReadOnlyList<CameraTriggerMode> TriggerModes { get; } =
        [CameraTriggerMode.Continuous, CameraTriggerMode.Software, CameraTriggerMode.External];

    [ObservableProperty]
    private CameraDescriptor? _selectedCamera;

    [ObservableProperty]
    private DeviceDescriptor? _selectedDevice;

    [ObservableProperty]
    private DeviceTagDisplayItem? _selectedDeviceTag;

    [ObservableProperty]
    private RobotDescriptor? _selectedRobot;

    [ObservableProperty]
    private bool _isLivePreviewActive;

    [ObservableProperty]
    private string _exposureUsText = "5000";

    [ObservableProperty]
    private string _gainDbText = "0";

    [ObservableProperty]
    private string _targetFpsText = "10";

    [ObservableProperty]
    private CameraTriggerMode _selectedTriggerMode = CameraTriggerMode.Continuous;

    [ObservableProperty]
    private string _tagWriteValueText = string.Empty;

    [ObservableProperty]
    private int _openCameraCount;

    [ObservableProperty]
    private int _runningCameraCount;

    [ObservableProperty]
    private int _onlineDeviceCount;

    [ObservableProperty]
    private int _onlineRobotCount;

    [ObservableProperty]
    private string _selectedCameraSummary = "未选择相机";

    [ObservableProperty]
    private string _selectedRobotSummary = "未选择机器人";

    partial void OnSelectedCameraChanged(CameraDescriptor? oldValue, CameraDescriptor? newValue)
    {
        NotifyCommands();
        UpdateSelectedCameraDisplay(newValue);
        if (oldValue?.Id != newValue?.Id)
        {
            StopLivePreview();
            CameraFeatures.Clear();
            _cameraViewer.SetImage(null, "所选相机尚无当前图像");
            _cameraViewer.SetOverlays([]);
            if (newValue is not null)
            {
                ExposureUsText = newValue.Settings.ExposureUs.ToString("0.##", CultureInfo.InvariantCulture);
                GainDbText = newValue.Settings.GainDb.ToString("0.##", CultureInfo.InvariantCulture);
                TargetFpsText = newValue.Settings.TargetFps.ToString("0.##", CultureInfo.InvariantCulture);
                SelectedTriggerMode = newValue.Settings.TriggerMode;
            }
        }
    }

    partial void OnSelectedDeviceChanged(DeviceDescriptor? value)
    {
        NotifyCommands();
        PopulateDeviceTags(value);
    }

    partial void OnSelectedDeviceTagChanged(DeviceTagDisplayItem? value)
    {
        NotifyCommands();
        if (value is not null) TagWriteValueText = value.ValueText;
    }

    partial void OnSelectedRobotChanged(RobotDescriptor? value)
    {
        NotifyCommands();
        UpdateSelectedRobotSummary(value);
    }

    partial void OnIsLivePreviewActiveChanged(bool value)
    {
        if (value) StartLivePreviewLoop();
        else StopLivePreviewLoop();
    }

    public Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    public void StopLivePreview()
    {
        if (IsLivePreviewActive) IsLivePreviewActive = false;
        else StopLivePreviewLoop();
    }

    private Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        RefreshCore(cancellationToken);
        return Task.CompletedTask;
    }

    private void RefreshCore(CancellationToken cancellationToken)
    {
        var snapshot = Host.MonitorReader.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        var prevCameraId = SelectedCamera?.Id;
        var prevDeviceId = SelectedDevice?.Id;
        var prevRobotId = SelectedRobot?.Id;

        Cameras.Clear();
        foreach (var c in snapshot.Cameras) Cameras.Add(c);
        SelectedCamera = prevCameraId is not null
            ? Cameras.FirstOrDefault(c => c.Id.Equals(prevCameraId, StringComparison.OrdinalIgnoreCase)) ?? Cameras.FirstOrDefault()
            : Cameras.FirstOrDefault();

        Devices.Clear();
        foreach (var d in snapshot.Devices) Devices.Add(d);
        SelectedDevice = prevDeviceId is not null
            ? Devices.FirstOrDefault(d => d.Id.Equals(prevDeviceId, StringComparison.OrdinalIgnoreCase)) ?? Devices.FirstOrDefault()
            : Devices.FirstOrDefault();

        Robots.Clear();
        foreach (var r in snapshot.Robots) Robots.Add(r);
        SelectedRobot = prevRobotId is not null
            ? Robots.FirstOrDefault(r => r.Id.Equals(prevRobotId, StringComparison.OrdinalIgnoreCase)) ?? Robots.FirstOrDefault()
            : Robots.FirstOrDefault();

        OpenCameraCount = Cameras.Count(IsCameraOpen);
        RunningCameraCount = Cameras.Count(IsCameraStreaming);
        OnlineDeviceCount = Devices.Count(d => d.ConnectionState == DeviceConnectionState.Connected);
        OnlineRobotCount = Robots.Count(r => r.ConnectionState == RobotConnectionState.Connected);

        UpdateSelectedCameraDisplay(SelectedCamera);
        PopulateDeviceTags(SelectedDevice);
        UpdateSelectedRobotSummary(SelectedRobot);

        ConfirmSnapshot();
        StatusMessage = $"设备快照已更新：相机 {Cameras.Count} 台，PLC/设备 {Devices.Count} 台，机器人 {Robots.Count} 台";
    }

    private static bool IsCameraOpen(CameraDescriptor camera)
        => camera.State is CameraState.Open or CameraState.Streaming;

    private static bool IsCameraStreaming(CameraDescriptor camera)
        => camera.State == CameraState.Streaming
           || camera.Acquisition.AcquisitionState is CameraAcquisitionState.Running or CameraAcquisitionState.WaitingTrigger;

    private void UpdateSelectedCameraDisplay(CameraDescriptor? camera)
    {
        SelectedCameraSummary = camera is null
            ? "未选择相机"
            : $"{camera.Name} ({camera.Id}) · 驱动: {camera.Driver} · 状态: {camera.State} / {camera.Acquisition.AcquisitionState} · 帧率 {camera.Acquisition.ActualFps:0.0} fps";
    }

    private void UpdateSelectedRobotSummary(RobotDescriptor? robot)
    {
        SelectedRobotSummary = robot is null
            ? "未选择机器人"
            : $"{robot.Name} ({robot.Id}){(robot.Simulated ? " [仿真]" : string.Empty)} · {robot.ConnectionState} / {robot.HandshakeState} · 当前位姿 ({robot.CurrentPose.X:0.##}, {robot.CurrentPose.Y:0.##}, {robot.CurrentPose.ThetaDeg:0.##}°)";
    }

    private void PopulateDeviceTags(DeviceDescriptor? device)
    {
        var selectedTagId = SelectedDeviceTag?.TagId;
        DeviceTags.Clear();
        if (device is null) return;
        foreach (var tag in device.Tags)
        {
            device.Values.TryGetValue(tag.Id, out var sample);
            DeviceTags.Add(new DeviceTagDisplayItem(
                tag.Id,
                tag.Name,
                tag.Address,
                tag.DataType,
                tag.Writable,
                sample?.AsText() ?? "-",
                sample?.Quality ?? DeviceTagQuality.Disconnected,
                sample?.Timestamp,
                tag.Unit));
        }
        SelectedDeviceTag = DeviceTags.FirstOrDefault(t => t.TagId == selectedTagId) ?? DeviceTags.FirstOrDefault();
    }

    private async Task OpenAndStartCameraAsync(CancellationToken cancellationToken)
    {
        var camera = SelectedCamera ?? throw new InvalidOperationException("请先选择相机。");
        await Host.Cameras.OpenAsync(camera.Id, cancellationToken);
        await Host.Cameras.StartAsync(camera.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"相机 {camera.Id} 已打开并开始采集";
    }

    private async Task StopAndCloseCameraAsync(CancellationToken cancellationToken)
    {
        StopLivePreview();
        var camera = SelectedCamera ?? throw new InvalidOperationException("请先选择相机。");
        await Host.Cameras.CloseAsync(camera.Id, cancellationToken);
        _cameraViewer.SetImage(null, "相机已关闭，旧图像已清除");
        _cameraViewer.SetOverlays([]);
        RefreshCore(cancellationToken);
        StatusMessage = $"相机 {camera.Id} 已停止并关闭";
    }

    private Task GrabLiveFrameAsync(CancellationToken cancellationToken)
        => GrabCameraFrameCoreAsync(triggerBeforeGrab: false, cancellationToken);

    private Task TriggerAndGrabAsync(CancellationToken cancellationToken)
        => GrabCameraFrameCoreAsync(triggerBeforeGrab: true, cancellationToken);

    private async Task GrabCameraFrameCoreAsync(bool triggerBeforeGrab, CancellationToken cancellationToken)
    {
        var camera = SelectedCamera ?? throw new InvalidOperationException("请先选择相机。");
        var (mat, sequence, timestamp) = await Host.GrabCameraMatAsync(camera.Id, triggerBeforeGrab: triggerBeforeGrab, cancellationToken: cancellationToken);
        using (mat)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedCamera?.Id != camera.Id)
                throw new InvalidOperationException("所选相机已经变化，请重新取图。");
            var bitmap = VisionImageInterop.ToBitmapSource(mat);
            _cameraViewer.SetImage(bitmap, $"{camera.Name} · 帧 #{sequence} ({timestamp:HH:mm:ss})", fitToView: !IsLivePreviewActive);
            _cameraViewer.SetOverlays([]);
        }
        RefreshCore(cancellationToken);
        StatusMessage = $"{(triggerBeforeGrab ? "软触发并" : string.Empty)}内存直采帧 #{sequence} ({camera.Id})";
    }

    private void StartLivePreviewLoop()
    {
        StopLivePreviewLoop();
        var camera = SelectedCamera;
        if (camera is null)
        {
            IsLivePreviewActive = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _livePreviewCts = cts;
        var cameraId = camera.Id;
        var cameraName = camera.Name;
        _livePreviewTask = Task.Run(async () =>
        {
            var first = true;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var (mat, sequence, timestamp) = await Host.GrabCameraMatAsync(cameraId, cancellationToken: cts.Token);
                    using (mat)
                    {
                        var bitmap = VisionImageInterop.ToBitmapSource(mat);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (cts.Token.IsCancellationRequested || SelectedCamera?.Id != cameraId) return;
                            _cameraViewer.SetImage(bitmap, $"{cameraName} · 连续预览 #{sequence} ({timestamp:HH:mm:ss})", fitToView: first);
                            _cameraViewer.SetOverlays([]);
                        });
                        first = false;
                    }
                    await Task.Delay(120, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"连续预览中断：{ex.Message}";
                        IsLivePreviewActive = false;
                    });
                    break;
                }
            }
        }, cts.Token);
    }

    private void StopLivePreviewLoop()
    {
        var cts = _livePreviewCts;
        _livePreviewCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task ApplyCameraSettingsAsync(CancellationToken cancellationToken)
    {
        var camera = SelectedCamera ?? throw new InvalidOperationException("请先选择相机。");
        if (!double.TryParse(ExposureUsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposure) || !double.IsFinite(exposure))
            throw new InvalidOperationException("曝光时间必须是有效数值。");
        if (!double.TryParse(GainDbText, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) || !double.IsFinite(gain))
            throw new InvalidOperationException("增益必须是有效数值。");
        if (!double.TryParse(TargetFpsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || !double.IsFinite(fps))
            throw new InvalidOperationException("目标帧率必须是有效数值。");

        var settings = (camera.Settings with
        {
            ExposureUs = exposure,
            GainDb = gain,
            TargetFps = fps,
            TriggerMode = SelectedTriggerMode
        }).Normalize();

        await Host.Cameras.ApplySettingsAsync(camera.Id, settings, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"已更新相机 {camera.Id} 参数：曝光 {settings.ExposureUs:0.#} us，增益 {settings.GainDb:0.#} dB，{settings.TriggerMode}";
    }

    private async Task LoadCameraFeaturesAsync(CancellationToken cancellationToken)
    {
        var camera = SelectedCamera ?? throw new InvalidOperationException("请先选择相机。");
        CameraFeatures.Clear();
        var caps = Host.Cameras.GetCommissioningCapabilities(camera.Id);
        if (!caps.FeatureBrowser)
        {
            StatusMessage = $"相机 {camera.Id} ({camera.Driver}) 未暴露 GenICam 特性浏览接口。";
            return;
        }
        var features = await Host.Cameras.ListFeaturesAsync(camera.Id, cancellationToken);
        foreach (var f in features) CameraFeatures.Add(f);
        StatusMessage = $"已读取相机 {camera.Id} 共 {CameraFeatures.Count} 项高级特性";
    }

    private async Task ConnectDeviceAsync(CancellationToken cancellationToken)
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择 PLC/工业设备。");
        await Host.Devices.ConnectAsync(device.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"设备 {device.Id} 已连接";
    }

    private async Task DisconnectDeviceAsync(CancellationToken cancellationToken)
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择 PLC/工业设备。");
        await Host.Devices.DisconnectAsync(device.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"设备 {device.Id} 已断开";
    }

    private async Task RefreshDeviceTagsAsync(CancellationToken cancellationToken)
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择 PLC/工业设备。");
        await Host.Devices.ReadAllFreshAsync(device.Id, autoConnect: true, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"已刷新设备 {device.Id} 共 {DeviceTags.Count} 个标签读数";
    }

    private async Task WriteDeviceTagAsync(CancellationToken cancellationToken)
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择 PLC/工业设备。");
        var tag = SelectedDeviceTag ?? throw new InvalidOperationException("请先选择要写入的标签。");
        if (!tag.Writable) throw new InvalidOperationException($"标签 {tag.TagId} 为只读标签。");
        var confirm = MessageBox.Show(
            $"确定向设备 {device.Name} ({device.Id}) 的标签 {tag.Name} ({tag.TagId}) 写入值“{TagWriteValueText}”吗？",
            "确认写入设备标签",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await Host.Devices.WriteTagAsync(device.Id, tag.TagId, TagWriteValueText, autoConnect: true, cancellationToken);
        await Host.Devices.ReadAllFreshAsync(device.Id, autoConnect: true, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"已写入标签 {device.Id}.{tag.TagId} = {TagWriteValueText}";
    }

    private async Task ConnectRobotAsync(CancellationToken cancellationToken)
    {
        var robot = SelectedRobot ?? throw new InvalidOperationException("请先选择机器人。");
        await Host.Robots.ConnectAsync(robot.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"机器人 {robot.Id} 已连接";
    }

    private async Task DisconnectRobotAsync(CancellationToken cancellationToken)
    {
        var robot = SelectedRobot ?? throw new InvalidOperationException("请先选择机器人。");
        await Host.Robots.DisconnectAsync(robot.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"机器人 {robot.Id} 已断开";
    }

    private async Task StopRobotAsync(CancellationToken cancellationToken)
    {
        var robot = SelectedRobot ?? throw new InvalidOperationException("请先选择机器人。");
        await Host.Robots.StopAsync(robot.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"已向机器人 {robot.Id} 发送停止指令";
    }

    private async Task ResetRobotFaultAsync(CancellationToken cancellationToken)
    {
        var robot = SelectedRobot ?? throw new InvalidOperationException("请先选择机器人。");
        await Host.Robots.ResetFaultAsync(robot.Id, cancellationToken);
        RefreshCore(cancellationToken);
        StatusMessage = $"机器人 {robot.Id} 故障已尝试复位";
    }

    public new async Task PrepareCloseAsync()
    {
        StopLivePreview();
        if (_livePreviewTask is not null)
        {
            try { await _livePreviewTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* 关闭预览等待超时/取消 */ }
        }
        await base.PrepareCloseAsync();
    }
}
