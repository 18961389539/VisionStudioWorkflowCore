using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Controls;
using ImageViewer.Core.Measurements;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;
using Viewer = ImageViewer.Controls.ImageViewer;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class MenuWorkflowIntegrationTests
{
    [Fact]
    public void OpenedMenuRegistersAdvancedCommandsAndMakesAliasesDirectSearchResults()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new Viewer();
            WpfTestRunner.InvokePrivate(viewer, "OnContextMenuOpened", viewer.mainContextMenu, new RoutedEventArgs());
            viewer.menuSearchBox.Text = "槽孔";
            WpfTestRunner.DrainDispatcher();
            var result = Assert.Single(viewer.mainContextMenu.Items.OfType<MenuItem>()
                .Where(item => item.Tag is MenuSearchResultTag { Original.Tag: ImageViewerAdvancedMeasurementMenuTag { Kind: AdvancedMeasurementKind.Slot } }));
            Assert.Same(viewer.mainContextMenu, ItemsControl.ItemsControlFromItemContainer(result));
            Assert.False(result.IsEnabled);
            Assert.Contains(UiText.Get("MenuNeedClosedContour"), result.ToolTip!.ToString(), StringComparison.Ordinal);
            viewer.menuSearchBox.Clear();
            // Five generated task groups; the fixed entries (advanced measurements, recipe) sit outside them.
            Assert.Equal(5, viewer.measureMenuItem.Items.OfType<MenuItem>()
                .Count(item => item.Tag is "measurement-task-group"));
        });
    }

    [Fact]
    public void ContextVertexActionAppearsOnceDeletesExplicitlyAndCanUndo()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new Viewer();
            var path = new PolylineRoi { Label = "edge", Points = new ObservableCollection<PointD> { new(0, 0), new(5, 1), new(10, 0) } };
            Assert.True(viewer.AddRoi(path));
            viewer.ViewerState.SelectedRoi = path;
            viewer.CaptureContextualPathVertex(path, 1);
            viewer.PrepareContextualRoiMenu();
            viewer.PrepareContextualRoiMenu();
            Assert.Equal(3, path.Points.Count);
            var delete = Assert.Single(viewer.contextRoiShortcutsMenuItem.Items.OfType<MenuItem>().Where(item => Equals(item.Header, UiText.Get("ContextRoiDeleteVertex"))));
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(2, path.Points.Count);
            viewer.ViewerState.UndoRedo.Undo();
            Assert.Equal(new PointD(5, 1), path.Points[1]);
        });
    }

    [Fact]
    public void DirectMeasurementLaunchPreselectsToolAndSourceWithoutSavingAnEdit()
    {
        WpfTestRunner.Run(() =>
        {
            var circle = new CircleRoi { Center = new(2, 3), Radius = 1 };
            int edits = 0;
            var dialog = new AdvancedMeasurementDialog(() => new RoiBase[] { circle }, () => 1, () => "px", () => null, (_, _) => edits++);
            dialog.BeginMeasurement(AdvancedMeasurementKind.Position, circle);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var combo = (ComboBox)typeof(AdvancedMeasurementDialog).GetField("_operationCombo", flags)!.GetValue(dialog)!;
            var sources = (ListBox)typeof(AdvancedMeasurementDialog).GetField("_selectedSourcesList", flags)!.GetValue(dialog)!;
            Assert.Equal(AdvancedMeasurementKind.Position, combo.SelectedValue);
            Assert.Single(sources.Items.Cast<object>());
            Assert.Equal(0, edits);
            Assert.Empty(circle.AdvancedMeasurements);
            dialog.Close();
        });
    }
}
