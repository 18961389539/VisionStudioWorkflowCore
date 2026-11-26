using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using VisionStudio.Abstractions;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.ViewModels;
using Xunit;

namespace VisionStudio.Wpf.Tests;

public sealed class WorkflowGraphAndViewModelTests
{
    private sealed class FakeViewerAdapter : IVisionImageViewerAdapter
    {
        public bool IsExternalImageViewerActive => false;
        public BitmapSource? LastImage { get; private set; }
        public string? LastCaption { get; private set; }
        public IReadOnlyList<VisionOverlay> LastOverlays { get; private set; } = [];
        public VisionRoi? ActiveUserRoi { get; set; }
        public VisionRoi? LastShownUserRoi { get; private set; }

        public void SetImage(BitmapSource? image, string? caption = null, bool fitToView = true)
        {
            LastImage = image;
            LastCaption = caption;
        }

        public void SetOverlays(IReadOnlyList<VisionOverlay>? overlays)
        {
            LastOverlays = overlays ?? [];
        }

        public void FitToView() { }
        public void ResetActualSize() { }
        public VisionRoi? TryGetActiveUserRoi() => ActiveUserRoi;
        public void ShowUserRoi(VisionRoi roi) => LastShownUserRoi = roi;
    }

    [Fact]
    public async Task DocumentMapper_AndNodeDetail_RoundTripParametersAccurately()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);

            var original = new WorkflowDefinition(
                "wf-roundtrip",
                "往返测试流程",
                [
                    new NodeDefinition("src-1", "image.synthetic", null, new NodePosition(40, 80), null),
                    new NodeDefinition("thr-1", "image.threshold", "自定义阈值", new NodePosition(320, 80),
                        new Dictionary<string, JsonElement> { ["threshold"] = JsonSerializer.SerializeToElement(120) })
                ],
                [
                    new EdgeDefinition("e1", "src-1", "next", "thr-1", "exec", EdgeKinds.Control),
                    new EdgeDefinition("e2", "src-1", "image", "thr-1", "image", EdgeKinds.Data)
                ]);

            var nodes = new ObservableCollection<WorkflowNodeViewModel>();
            var connections = new ObservableCollection<WorkflowConnectionViewModel>();
            WorkflowDocumentMapper.LoadIntoGraph(original, host.NodeRegistry, nodes, connections);

            Assert.Equal(2, nodes.Count);
            Assert.Equal(2, connections.Count);
            Assert.True(nodes[0].Outputs.First(p => p.PortName == "image").IsConnected);

            // 模拟双击节点弹出详情窗口并修改阈值参数与标题
            var detailVm = new NodeDetailViewModel(nodes[1]);
            detailVm.NodeTitle = "高对比阈值";
            var thresholdParam = detailVm.Parameters.First(p => p.Name.Equals("threshold", StringComparison.OrdinalIgnoreCase));
            thresholdParam.TextValue = "185";
            Assert.True(detailVm.TryApplyChanges());

            var exported = WorkflowDocumentMapper.BuildDefinition("wf-roundtrip", "往返测试流程", nodes, connections);
            Assert.Equal(2, exported.Nodes.Count);
            Assert.Equal(2, exported.Edges.Count);
            var exportedThr = exported.Nodes.First(n => n.Id == "thr-1");
            Assert.Equal("高对比阈值", exportedThr.Name);
            Assert.Equal(185, exportedThr.Parameters!["threshold"].GetDouble());
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConnectorViewModel_EnforcesTypeAndDirectionRules()
    {
        var nodeA = new WorkflowNodeViewModel("a", "image.synthetic", null, "A", new Point(0, 0), null);
        var nodeB = new WorkflowNodeViewModel("b", "image.threshold", null, "B", new Point(100, 0), null);

        var aNext = new ConnectorViewModel(nodeA, "next", VisionDataType.Control, isInput: false, required: false);
        var aImgOut = new ConnectorViewModel(nodeA, "image", VisionDataType.Image, isInput: false, required: false);
        var aImgIn = new ConnectorViewModel(nodeA, "image", VisionDataType.Image, isInput: true, required: false);

        var bExec = new ConnectorViewModel(nodeB, "exec", VisionDataType.Control, isInput: true, required: false);
        var bImgIn = new ConnectorViewModel(nodeB, "image", VisionDataType.Image, isInput: true, required: true);
        var bNumIn = new ConnectorViewModel(nodeB, "threshold", VisionDataType.Double, isInput: true, required: false);
        var bAnyIn = new ConnectorViewModel(nodeB, "value", VisionDataType.Any, isInput: true, required: false);

        Assert.True(ConnectorViewModel.CanConnect(aNext, bExec));
        Assert.True(ConnectorViewModel.CanConnect(aImgOut, bImgIn));
        Assert.True(ConnectorViewModel.CanConnect(aImgOut, bAnyIn));
        Assert.False(ConnectorViewModel.CanConnect(aNext, bImgIn));
        Assert.False(ConnectorViewModel.CanConnect(aImgOut, bNumIn));
        Assert.False(ConnectorViewModel.CanConnect(aImgOut, aImgIn)); // 禁止同节点自环
    }

    [Fact]
    public async Task WorkflowEditorViewModel_RunsInProcess_AndUpdatesViewerAndReports()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });

            await editor.ValidateWorkflowCommand.ExecuteAsync(null);
            Assert.Contains("编译校验通过", editor.StatusMessage);

            await editor.RunWorkflowCommand.ExecuteAsync(null);
            Assert.NotEmpty(editor.LatestNodeReports);
            Assert.NotNull(viewer.LastImage);
            Assert.Contains("成功", editor.LastRunSummary);
            Assert.Equal("Succeeded", editor.LastRunStateKey);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EditorToolboxSearch_AutoExpandsMatches_AndDropPlacementSetsLocation()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });

            editor.SearchKeyword = "threshold";
            Assert.NotEmpty(editor.FilteredToolbox);
            Assert.All(editor.FilteredToolbox, group => Assert.True(group.IsExpanded));

            var targetItem = editor.FilteredToolbox.SelectMany(g => g.Items).First();
            var added = editor.AddNodeFromToolboxAt(targetItem, new Point(420, 260));
            Assert.NotNull(added);
            Assert.Equal(new Point(420, 260), added.Location);

            editor.ClearSearchCommand.Execute(null);
            Assert.Equal(string.Empty, editor.SearchKeyword);
            Assert.True(editor.FilteredToolbox[0].IsExpanded);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceViewModels_PopulateSummaryMetrics_OnRefresh()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();

            var production = new ProductionRunViewModel(host);
            await production.RefreshAsync();
            Assert.Equal(production.Stations.Count, production.TotalStations);
            Assert.Equal(production.ActiveAlarms.Count, production.ActiveAlarmCount);

            var devices = new DeviceMonitorViewModel(host, viewer);
            await devices.RefreshAsync();
            Assert.NotNull(devices.SelectedCamera);
            Assert.Contains(devices.SelectedCamera!.Id, devices.SelectedCameraSummary);

            var diagnostics = new TraceDiagnosticsViewModel(host);
            diagnostics.TraceFilter = "sample";
            diagnostics.ClearTraceFilterCommand.Execute(null);
            Assert.Equal(string.Empty, diagnostics.TraceFilter);
            await diagnostics.RefreshAsync();
            var summary = host.Diagnostics.Summary();
            Assert.Equal(summary.Healthy, diagnostics.HealthyCount);
            Assert.Equal(summary.Degraded, diagnostics.DegradedCount);
            Assert.Equal(summary.Faulted, diagnostics.FaultedCount);
            Assert.Equal(summary.Offline, diagnostics.OfflineCount);
            Assert.Equal(summary.Total, diagnostics.AssetHealth.Count);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkflowEditor_UndoRedo_CopyPaste_AndAutoLayout_WorkEndToEnd()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });

            var initialCount = editor.Nodes.Count;
            var thresholdItem = editor.FilteredToolbox.SelectMany(g => g.Items).First(i => i.Type == "image.threshold");
            var added = editor.AddNodeFromToolboxAt(thresholdItem, new Point(50, 400));
            Assert.NotNull(added);
            Assert.Equal(initialCount + 1, editor.Nodes.Count);
            Assert.True(editor.CanUndo);

            editor.UndoCommand.Execute(null);
            Assert.Equal(initialCount, editor.Nodes.Count);
            Assert.True(editor.CanRedo);

            editor.RedoCommand.Execute(null);
            Assert.Equal(initialCount + 1, editor.Nodes.Count);

            // 复制并粘贴选中节点
            editor.SelectedNodes.Clear();
            editor.SelectedNodes.Add(editor.Nodes[0]);
            editor.SelectedNodes.Add(editor.Nodes[1]);
            editor.CopySelectedNodesCommand.Execute(null);
            Assert.True(editor.CanPaste);
            var beforePaste = editor.Nodes.Count;
            editor.PasteNodesCommand.Execute(null);
            Assert.Equal(beforePaste + 2, editor.Nodes.Count);

            // 自动排版应能调整节点位置并支持撤销
            editor.Nodes[0].Location = new Point(900, 900);
            editor.AutoLayoutCommand.Execute(null);
            Assert.True(editor.Nodes[0].Location.X < editor.Nodes[1].Location.X);
            editor.UndoCommand.Execute(null);
            Assert.Equal(900, editor.Nodes[0].Location.X);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WorkflowEditor_BreakpointDebug_AndRunToNode_ExecuteInProcess()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });

            Assert.True(editor.Nodes.Count >= 2);
            var target = editor.Nodes[1];
            editor.SelectedNode = target;

            // 运行至目标节点
            await editor.RunToSelectedNodeCommand.ExecuteAsync(null);
            Assert.Contains("成功", editor.LastRunSummary);

            // 断点调试：暂停 -> 缓存单步重跑 -> 继续完成
            editor.ToggleBreakpointCommand.Execute(null);
            Assert.True(target.HasBreakpoint);
            await editor.StartOrContinueDebugCommand.ExecuteAsync(null);
            Assert.True(editor.HasActiveDebugSession);
            Assert.True(editor.IsDebugHalted);
            Assert.Equal(NodeExecutionVisualState.Paused, target.ExecutionState);

            await editor.RunCachedDebugNodeCommand.ExecuteAsync(null);
            Assert.True(editor.HasActiveDebugSession);

            await editor.StartOrContinueDebugCommand.ExecuteAsync(null);
            await editor.StopDebugSessionCommand.ExecuteAsync(null);
            Assert.False(editor.HasActiveDebugSession);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NodeDetailViewModel_ExposesRoiParameters_AndImportsFromViewer()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var cropCatalog = host.NodeRegistry.Catalog.First(item => item.Type == "image.crop");
            var cropNode = new WorkflowNodeViewModel("crop-1", cropCatalog.Type, cropCatalog, null, new Point(100, 100), null);
            var expectedRoi = new VisionRoi(VisionRoiType.Rectangle, X: 12, Y: 24, Width: 80, Height: 60);
            VisionRoi? previewed = null;

            var detail = new NodeDetailViewModel(
                cropNode,
                getViewerRoi: () => expectedRoi,
                previewViewerRoi: r => previewed = r);

            var roiParam = Assert.Single(detail.Parameters, p => p.IsRoi && p.Name == "roi");
            detail.ImportRoiFromViewerCommand.Execute(roiParam);
            Assert.Contains("矩形", roiParam.RoiSummaryText);
            detail.PreviewRoiInViewerCommand.Execute(roiParam);
            Assert.Equal(expectedRoi, previewed);

            Assert.True(detail.TryApplyChanges());
            var savedRoi = new NodeDefinition(cropNode.Id, cropNode.Type, null, null, cropNode.Parameters).GetRoi();
            Assert.Equal(expectedRoi, savedRoi);

            detail.ClearRoiCommand.Execute(roiParam);
            Assert.True(detail.TryApplyChanges());
            Assert.False(cropNode.Parameters.ContainsKey("roi"));
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NodeDetail_ParameterFilter_PreservesActiveValues_AndConnectorHasTooltip()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var thresholdCatalog = host.NodeRegistry.Catalog.First(item => item.Type == "image.threshold");
            var node = new WorkflowNodeViewModel("thr-1", thresholdCatalog.Type, thresholdCatalog, null, new Point(0, 0), null);
            Assert.Contains(node.Inputs, input => input.TooltipText.Contains("输入", StringComparison.Ordinal));

            var detail = new NodeDetailViewModel(node);
            Assert.Equal(detail.ActiveParameterCount, detail.VisibleParameterCount);
            var thresholdParam = detail.Parameters.First(p => p.Name.Equals("threshold", StringComparison.OrdinalIgnoreCase));
            thresholdParam.TextValue = "142";

            detail.ParameterFilter = "__unlikely_no_match__";
            Assert.Equal(0, detail.VisibleParameterCount);
            Assert.True(detail.HasNoVisibleParameters);
            // 筛选隐藏时仍校验并保留所有有效参数值
            Assert.True(detail.TryApplyChanges());
            Assert.Equal(142, node.Parameters["threshold"].GetDouble());

            detail.ClearParameterFilterCommand.Execute(null);
            Assert.Equal(detail.ActiveParameterCount, detail.VisibleParameterCount);
            Assert.False(detail.HasNoVisibleParameters);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeviceAndDiagnostics_DeepOperations_WorkWithInProcessHost()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);
            var viewer = new FakeViewerAdapter();

            // 设备：打开相机、改参、取图、读取设备标签、关闭
            var devices = new DeviceMonitorViewModel(host, viewer);
            await devices.RefreshAsync();
            Assert.NotNull(devices.SelectedCamera);
            await devices.OpenAndStartCameraCommand.ExecuteAsync(null);
            devices.ExposureUsText = "6500";
            devices.GainDbText = "2.5";
            await devices.ApplyCameraSettingsCommand.ExecuteAsync(null);
            await devices.GrabLiveFrameCommand.ExecuteAsync(null);
            Assert.NotNull(viewer.LastImage);
            await devices.StopAndCloseCameraCommand.ExecuteAsync(null);

            if (devices.SelectedDevice is not null)
            {
                await devices.ConnectDeviceCommand.ExecuteAsync(null);
                await devices.RefreshDeviceTagsCommand.ExecuteAsync(null);
                Assert.NotEmpty(devices.DeviceTags);
                await devices.DisconnectDeviceCommand.ExecuteAsync(null);
            }

            // 编辑器运行一次后验证追溯、耗时遥测与 SPC
            var editor = new WorkflowEditorViewModel(host, viewer, (_, _) => { });
            await editor.RunWorkflowCommand.ExecuteAsync(null);

            WorkflowDefinition? loadedBack = null;
            var diagnostics = new TraceDiagnosticsViewModel(host, () => editor.ExportCurrentDefinition(), wf => loadedBack = wf);
            await diagnostics.RefreshAsync();
            Assert.NotEmpty(diagnostics.TraceRecords);
            Assert.NotEmpty(diagnostics.NodeDurations);
            Assert.NotEmpty(diagnostics.RecentNodeSpans);

            diagnostics.SelectedTrace = diagnostics.TraceRecords[0];
            await diagnostics.ViewTraceCommand.ExecuteAsync(null);
            Assert.NotEmpty(diagnostics.SelectedTraceNodeReports);
            await diagnostics.LoadTraceWorkflowToEditorCommand.ExecuteAsync(null);
            Assert.NotNull(loadedBack);

            var production = new ProductionRunViewModel(host, viewer, () => editor.ExportCurrentDefinition());
            await production.InitializeTrialCommand.ExecuteAsync(null);
            Assert.NotEmpty(production.SelectedJobVersions);
            await production.AnalyzeSpcCommand.ExecuteAsync(null);
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }
}
