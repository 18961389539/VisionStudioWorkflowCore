using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStudio.Engine;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.ViewModels;

namespace VisionStudio.Wpf.Views;

/// <summary>
/// 工作流编排界面视图逻辑：处理工具箱双击/拖拽加节点、画布缩放适配、Delete 删节点、节点详情弹窗等交互。
/// </summary>
public partial class WorkflowEditorView : UserControl
{
    private Point? _toolboxDragStart;
    private ToolboxNodeItem? _toolboxDragItem;

    public WorkflowEditorView()
    {
        InitializeComponent();
    }

    public IVisionImageViewerAdapter ImageViewerAdapter => ImageViewerControl;

    private void OnToolboxItemPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement { DataContext: ToolboxNodeItem item })
        {
            _toolboxDragStart = e.GetPosition(this);
            _toolboxDragItem = item;
        }
    }

    private void OnToolboxItemPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _toolboxDragStart is null || _toolboxDragItem is null)
        {
            return;
        }

        var delta = e.GetPosition(this) - _toolboxDragStart.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _toolboxDragItem;
        _toolboxDragStart = null;
        _toolboxDragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ToolboxNodeItem), item), DragDropEffects.Copy);
    }

    private void OnToolboxItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: ToolboxNodeItem item }
            && DataContext is WorkflowEditorViewModel vm)
        {
            _toolboxDragStart = null;
            _toolboxDragItem = null;
            var added = vm.AddNodeFromToolboxAt(item, null);
            if (added is not null)
            {
                FocusNodeInViewport(added);
            }
        }
    }

    private void OnGraphDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ToolboxNodeItem)) && DataContext is WorkflowEditorViewModel { CanEdit: true })
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnGraphDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ToolboxNodeItem)) is ToolboxNodeItem item
            && DataContext is WorkflowEditorViewModel vm)
        {
            var dropPos = GraphEditor.GetLocationInsideEditor(e);
            vm.AddNodeFromToolboxAt(item, dropPos);
            e.Handled = true;
        }
    }

    private void OnGraphPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete
            && e.OriginalSource is not TextBox
            && DataContext is WorkflowEditorViewModel vm
            && vm.DeleteSelectedNodesCommand.CanExecute(null))
        {
            vm.DeleteSelectedNodesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNodePreviewRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkflowNodeViewModel node }
            && DataContext is WorkflowEditorViewModel vm)
        {
            if (!vm.SelectedNodes.Contains(node))
            {
                vm.SelectedNode = node;
                vm.SelectedNodes.Clear();
                vm.SelectedNodes.Add(node);
            }
        }
    }

    private void OnNodeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkflowNodeViewModel node }
            && DataContext is WorkflowEditorViewModel vm)
        {
            vm.OpenNodeDetailCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void OnReportRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: NodeRunReport report }
            && DataContext is WorkflowEditorViewModel vm)
        {
            var node = vm.Nodes.FirstOrDefault(n => n.Id.Equals(report.NodeId, StringComparison.OrdinalIgnoreCase));
            if (node is not null) FocusNodeInViewport(node);
            vm.InspectReportNode(report);
        }
    }

    private void OnFitGraphClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkflowEditorViewModel { Nodes.Count: > 0 })
        {
            GraphEditor.FitToScreen();
        }
        else
        {
            GraphEditor.ResetViewport();
        }
    }

    private void OnZoomInGraphClick(object sender, RoutedEventArgs e) => GraphEditor.ZoomIn();

    private void OnZoomOutGraphClick(object sender, RoutedEventArgs e) => GraphEditor.ZoomOut();

    private void OnResetViewportClick(object sender, RoutedEventArgs e) => GraphEditor.ResetViewport();

    private void OnToggleShortcutsHelpClick(object sender, RoutedEventArgs e)
    {
        ShortcutsHelpPopup.IsOpen = !ShortcutsHelpPopup.IsOpen;
    }

    private void OnValidationIssueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: WorkflowValidationIssue issue }
            && DataContext is WorkflowEditorViewModel vm)
        {
            vm.FocusValidationIssueCommand.Execute(issue);
            if (vm.SelectedNode is not null)
            {
                FocusNodeInViewport(vm.SelectedNode);
            }
            e.Handled = true;
        }
    }

    private void FocusNodeInViewport(WorkflowNodeViewModel node)
    {
        GraphEditor.BringIntoView(new Rect(node.Location.X - 40, node.Location.Y - 30, 280, 180));
    }
}
