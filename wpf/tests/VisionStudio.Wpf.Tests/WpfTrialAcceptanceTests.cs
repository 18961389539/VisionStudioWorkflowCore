using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.ViewModels;
using Xunit;

namespace VisionStudio.Wpf.Tests;

[Collection("WPF UI")]
public sealed class WpfTrialAcceptanceTests
{
    private readonly WpfDispatcherFixture _dispatcher;

    public WpfTrialAcceptanceTests(WpfDispatcherFixture dispatcher) => _dispatcher = dispatcher;

    private sealed class Viewer : IVisionImageViewerAdapter
    {
        public bool IsExternalImageViewerActive => false;
        public BitmapSource? Image { get; private set; }
        public void SetImage(BitmapSource? image, string? caption = null, bool fitToView = true) => Image = image;
        public void SetOverlays(IReadOnlyList<VisionOverlay>? overlays) { }
        public void FitToView() { }
        public void ResetActualSize() { }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"visionstudio-trial-{Guid.NewGuid():N}");

    private sealed class FailedWorkspace : DesktopWorkspaceViewModel
    {
        public FailedWorkspace(InProcessVisionHost host) : base(host) =>
            RefreshCommand = CreateCommand("模拟刷新", _ => throw new IOException("模拟状态读取失败"));
        public IAsyncRelayCommand RefreshCommand { get; }
    }

    [Fact]
    public async Task FailedRefresh_MarksStateUnknown_AndReportsTheError()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var workspace = new FailedWorkspace(host) { IsStatusConfirmed = true };
            await workspace.RefreshCommand.ExecuteAsync(null);
            Assert.False(workspace.IsStatusConfirmed);
            Assert.Contains("未知", workspace.FreshnessText);
            Assert.Contains("模拟状态读取失败", workspace.StatusMessage);
            Assert.Contains(host.LogSink.Snapshot(), entry => entry.Level == "错误" && entry.ExceptionText?.Contains("模拟状态读取失败") == true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledStartup_ReleasesPartialHostResources()
    {
        var root = NewRoot();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InProcessVisionHost.StartNewAsync(root, cancellationToken: new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RequiredStringCannotBeEmpty_AndProfileIdIsNotAFilePath()
    {
        var required = new NodeParameterItemViewModel(new ParameterDescriptor("name", "名称", "string"), null);
        Assert.False(required.TryBuildJsonElement(out _, out var error));
        Assert.Contains("不能为空", error);
        var profile = new NodeParameterItemViewModel(new ParameterDescriptor("profileId", "配置 ID", "string"), null);
        Assert.False(profile.IsPath);
    }

    [Fact]
    public async Task DataRootIsExclusive_AndCanBeReopenedAfterShutdown()
    {
        var root = NewRoot();
        try
        {
            await using (var first = await InProcessVisionHost.StartNewAsync(root))
            {
                var error = await Assert.ThrowsAsync<IOException>(() => InProcessVisionHost.StartNewAsync(root));
                Assert.Contains("无法独占桌面数据根", error.Message);
                Assert.Contains(root, error.Message);
            }
            await using var reopened = await InProcessVisionHost.StartNewAsync(root);
            Assert.NotEmpty(reopened.NodeRegistry.Catalog);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PublishedHostRejectsDataInsideItsPackage()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "build-info.json"), "{}");
            var data = Path.Combine(root, "data");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InProcessVisionHost.StartNewAsync(data, root));
            Assert.Contains("版本目录之外", error.Message);
            Assert.False(Directory.Exists(data));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParameterVisibility_SkipsInactiveValues_AndRevalidatesWhenEnabled()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var catalog = host.NodeRegistry.Catalog.First(item => item.Type == "image.synthetic") with
            {
                Parameters =
                [
                    new ParameterDescriptor("mode", "模式", "string", "auto",
                        Options: [new ParameterOption("自动", "auto"), new ParameterOption("文件", "file")]),
                    new ParameterDescriptor("path", "路径", "string", VisibleWhen: new ParameterVisibility("mode", ["file"])),
                    new ParameterDescriptor("portValue", "端口替代值", "number") { OverriddenByPorts = ["value"] }
                ]
            };
            var node = new WorkflowNodeViewModel("source", catalog.Type, catalog, null, new Point(), null);
            var input = new ConnectorViewModel(node, "value", VisionDataType.Any, isInput: true, required: false) { IsConnected = true };
            node.Inputs.Add(input);
            var detail = new NodeDetailViewModel(node);
            var path = detail.Parameters.Single(parameter => parameter.Name == "path");
            Assert.False(path.IsActive);
            Assert.True(detail.TryApplyChanges());
            detail.Parameters.Single(parameter => parameter.Name == "mode").TextValue = "file";
            Assert.True(path.IsActive);
            Assert.False(detail.TryApplyChanges());
            path.TextValue = "sample.png";
            Assert.True(detail.TryApplyChanges());
            var portValue = detail.Parameters.Single(parameter => parameter.Name == "portValue");
            Assert.False(portValue.IsActive);
            input.IsConnected = false;
            detail.Parameters.Single(parameter => parameter.Name == "mode").TextValue = "auto";
            Assert.True(portValue.IsActive);
            Assert.False(detail.TryApplyChanges());
            portValue.TextValue = "12";
            Assert.True(detail.TryApplyChanges());
            input.IsConnected = true;
            portValue.TextValue = "invalid";
            path.TextValue = "";
            Assert.True(detail.TryApplyChanges());
            Assert.Equal(12, node.Parameters["portValue"].GetDouble());
            Assert.Equal("sample.png", node.Parameters["path"].GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("256")]
    public void Parameter_RejectsNonFiniteOrOutOfRangeNumbers(string input)
    {
        var parameter = new NodeParameterItemViewModel(new ParameterDescriptor("threshold", "阈值", "number", Min: 0, Max: 255), null)
        { TextValue = input };
        Assert.False(parameter.TryBuildJsonElement(out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parameter_RejectsFractionalInteger_InvalidOption_AndEmptyJson()
    {
        var integer = new NodeParameterItemViewModel(new ParameterDescriptor("count", "数量", "number") { Integer = true }, null)
        { TextValue = "2.5" };
        Assert.False(integer.TryBuildJsonElement(out _, out _));
        var option = new NodeParameterItemViewModel(new ParameterDescriptor("mode", "模式", "string",
            Options: [new ParameterOption("有效", "valid")]), null)
        { TextValue = "missing" };
        Assert.False(option.TryBuildJsonElement(out _, out _));
        var json = new NodeParameterItemViewModel(new ParameterDescriptor("settings", "配置", "json"), null);
        Assert.False(json.TryBuildJsonElement(out _, out _));
        var optional = new NodeParameterItemViewModel(new ParameterDescriptor("value", "可选", "number") { Optional = true }, null);
        Assert.True(optional.TryBuildJsonElement(out var unset, out _));
        Assert.Equal(JsonValueKind.Undefined, unset.ValueKind);
    }

    [Fact]
    public async Task InvalidDocument_DoesNotReplaceGraph_AndFutureSchemaIsRejected()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var editor = new WorkflowEditorViewModel(host, new Viewer(), (_, _) => { });
            var original = editor.ExportCurrentDefinition();
            var invalid = original with { Edges = [new EdgeDefinition("invalid", "missing", "image", original.Nodes[0].Id, "image")] };
            Assert.Throws<InvalidOperationException>(() => editor.LoadDefinition(invalid, true));
            Assert.Equal(original.Nodes.Count, editor.Nodes.Count);
            Assert.Equal(original.Id, editor.WorkflowId);
            Assert.Throws<ApiConflictException>(() => editor.LoadDefinition(original with { SchemaVersion = WorkflowDocumentSchema.CurrentVersion + 1 }, true));
            Assert.Equal(original.Edges.Count, editor.Connections.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Editor_ProtectsUnsavedChangesAndRevision_AndClearsOldImage()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var viewer = new Viewer();
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { }, () => UnsavedChangesDecision.Cancel);
            Assert.True(await editor.SaveCurrentAsync());
            var originalId = editor.WorkflowId;
            editor.WorkflowName = "未保存的修改";
            Assert.True(editor.IsDirty);
            await editor.NewWorkflowCommand.ExecuteAsync(null);
            Assert.Equal(originalId, editor.WorkflowId);
            var revision = await host.WorkflowStore.GetRevisionAsync(originalId, CancellationToken.None);
            await host.WorkflowStore.SaveAsync(originalId, editor.ExportCurrentDefinition() with { Name = "其他编辑" }, CancellationToken.None, revision);
            Assert.False(await editor.SaveCurrentAsync());
            Assert.True(editor.IsDirty);
            Assert.Contains("保存失败", editor.StatusMessage);
            await editor.RunWorkflowCommand.ExecuteAsync(null);
            Assert.NotNull(viewer.Image);
            editor.LoadDefinition(new WorkflowDefinition("empty", "空流程", [], []), true);
            Assert.Null(viewer.Image);
            Assert.Empty(editor.LatestNodeReports);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrialInitializer_IsIdempotent_AndStationRuns1000Cycles()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var first = await DesktopTrialInitializer.InitializeAsync(host);
            var second = await DesktopTrialInitializer.InitializeAsync(host);
            Assert.Equal(1, first.PublishedVersion);
            Assert.Equal(first.UpdatedAt, second.UpdatedAt);
            Assert.Equal(ProductionRuntimeState.Stopped, host.Production.Status.State);
            var production = new ProductionRunViewModel(host);
            await production.RefreshAsync();
            Assert.True(production.IsStatusConfirmed);
            Assert.True(production.StartProductionCommand.CanExecute(null));
            await production.StartProductionCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (host.Production.Status.CycleCount < 1000 && DateTime.UtcNow < deadline)
            {
                Assert.Equal(ProductionRuntimeState.Running, host.Production.Status.State);
                await Task.Delay(50);
            }
            Assert.True(host.Production.Status.CycleCount >= 1000);
            await production.RefreshAsync();
            Assert.Same(production.Stations.Single(), production.SelectedStation);
            await production.StopProductionCommand.ExecuteAsync(null);
            Assert.Equal(ProductionRuntimeState.Stopped, host.Production.Status.State);
            var cycles = host.Production.Status.CycleCount;
            await Task.Delay(100);
            Assert.Equal(cycles, host.Production.Status.CycleCount);
            Assert.NotEmpty(await host.Traces.ListAsync(10, first.Id, null, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Devices_Alarms_TraceDetails_AndDiagnosticBundle_WorkWithoutHardware()
    {
        var root = NewRoot();
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(root);
            var viewer = new Viewer();
            var devices = new DeviceMonitorViewModel(host, viewer);
            Assert.False(devices.GrabLiveFrameCommand.CanExecute(null));
            await devices.RefreshAsync();
            await devices.OpenAndStartCameraCommand.ExecuteAsync(null);
            await devices.GrabLiveFrameCommand.ExecuteAsync(null);
            Assert.NotNull(viewer.Image);
            await devices.StopAndCloseCameraCommand.ExecuteAsync(null);
            Assert.Null(viewer.Image);
            await host.Alarms.RaiseAsync("WPF-TRIAL", AlarmSeverity.Warning, "trial", "模拟报警", null, CancellationToken.None);
            var production = new ProductionRunViewModel(host);
            await production.RefreshAsync();
            production.SelectedAlarm = production.ActiveAlarms.Single(alarm => alarm.Code == "WPF-TRIAL");
            await production.AcknowledgeAlarmCommand.ExecuteAsync(null);
            Assert.True(production.ActiveAlarms.Single(alarm => alarm.Code == "WPF-TRIAL").Acknowledged);
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });
            await editor.RunWorkflowCommand.ExecuteAsync(null);
            var diagnostics = new TraceDiagnosticsViewModel(host);
            await diagnostics.RefreshAsync();
            diagnostics.SelectedTrace = diagnostics.TraceRecords.First();
            await diagnostics.ViewTraceCommand.ExecuteAsync(null);
            Assert.Contains(diagnostics.SelectedTrace.RunId, diagnostics.TraceDetails);
            var path = Path.Combine(root, "diagnostic.zip");
            await diagnostics.ExportBundleAsync(path);
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.Null(zip.GetEntry("appsettings.json"));
            await devices.PrepareCloseAsync();
            Assert.False(devices.GrabLiveFrameCommand.CanExecute(null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Window_CloseDuringRunAndProduction_ReleasesDataRootFor20Rounds()
    {
        var root = NewRoot();
        try
        {
            await OnStaAsync(async () =>
            {
                EnsureApplication();
                for (var round = 0; round < 20; round++)
                {
                    await using var host = await InProcessVisionHost.StartNewAsync(root);
                    var runtime = host.Production;
                    await DesktopTrialInitializer.InitializeAsync(host);
                    var window = new MainWindow(host);
                    window.Show();
                    var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    await vm.Production.RefreshAsync();
                    await vm.Production.StartProductionCommand.ExecuteAsync(null);
                    Assert.Equal(ProductionRuntimeState.Running, host.Production.Status.State);
                    vm.Editor.LoadDefinition(new WorkflowDefinition("delayed", "取消测试",
                        [new NodeDefinition("delay", "flow.delay", null, null,
                            new Dictionary<string, JsonElement> { ["delayMs"] = JsonSerializer.SerializeToElement(10000) })], []), true);
                    var run = vm.Editor.RunWorkflowCommand.ExecuteAsync(null);
                    Assert.False(vm.Editor.CanEdit);
                    Assert.False(vm.Editor.NewWorkflowCommand.CanExecute(null));
                    vm.Editor.SelectedNode = vm.Editor.Nodes[0];
                    vm.Editor.DeleteSelectedNodesCommand.Execute(null);
                    await vm.Editor.NewWorkflowCommand.ExecuteAsync(null);
                    Assert.Single(vm.Editor.Nodes);
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.SetResult();
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    await run;
                    Assert.Equal(ProductionRuntimeState.Stopped, runtime.Status.State);
                    Assert.False(window.IsVisible);
                }
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1366, 768, 1.0)]
    [InlineData(1366, 768, 1.25)]
    [InlineData(1366, 768, 1.5)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 1.25)]
    [InlineData(1920, 1080, 1.5)]
    public async Task Window_LogicalViewportMatrix_KeepsWorkspaceCommandsReachable(int width, int height, double scale)
    {
        var root = NewRoot();
        try
        {
            await OnStaAsync(async () =>
            {
                EnsureApplication();
                await using var host = await InProcessVisionHost.StartNewAsync(root);
                var window = new MainWindow(host) { Width = width / scale, Height = height / scale };
                window.Show();
                var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
                for (var tab = 0; tab < 4; tab++)
                {
                    vm.SelectedTabIndex = tab;
                    window.UpdateLayout();
                    foreach (var button in Descendants<Button>(window).Where(button => button.IsVisible
                        && button.Command is not null && button.DataContext is DesktopWorkspaceViewModel or WorkflowEditorViewModel))
                    {
                        var point = button.TranslatePoint(new Point(), window);
                        Assert.InRange(point.Y, 0, window.ActualHeight - 1);
                        Assert.InRange(point.X, 0, window.ActualWidth - 1);
                        Assert.True(point.X + button.ActualWidth <= window.ActualWidth, $"Clipped command: {button.Content}");
                    }
                }
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.SetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            });
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
    }

    private Task OnStaAsync(Func<Task> action) => _dispatcher.RunAsync(action);
}
