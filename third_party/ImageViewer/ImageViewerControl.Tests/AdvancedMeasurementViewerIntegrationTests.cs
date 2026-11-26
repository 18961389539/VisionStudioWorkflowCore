using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.ViewModels;
using Xunit;
using Viewer = ImageViewer.Controls.ImageViewer;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class AdvancedMeasurementViewerIntegrationTests
{
    [Fact]
    public void WorkbenchEditIsUndoableAndLiveSourcesRemainAssociated()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new Viewer();
            var first = new CircleRoi { Center = new(0, 0), Radius = 2 };
            var second = new CircleRoi { Center = new(10, 0), Radius = 2 };
            viewer.ViewerState.ReplaceAllRois([first, second]);
            var definition = new AdvancedMeasurementDefinition { Kind = AdvancedMeasurementKind.ShortestDistance, Sources = [first.MeasurementId, second.MeasurementId] };
            viewer.SetAdvancedMeasurements(first, [definition]);
            Assert.Single(first.AdvancedMeasurements);
            viewer.ViewerState.UndoRedo.Undo();
            Assert.Empty(first.AdvancedMeasurements);
            viewer.ViewerState.UndoRedo.Redo();
            Assert.Equal(definition.Id, Assert.Single(first.AdvancedMeasurements).Id);
            second.Center = new(20, 0);
            var measured = AdvancedMeasurementService.EvaluateAll(viewer.ViewerState.AllRois, 0.5, "mm");
            Assert.Equal(8, Assert.Single(measured).Metrics[0].Value);
            viewer.ViewerState.UndoRedo.Execute(new RemoveRoiCommand(second, viewer.ViewerState));
            Assert.False(Assert.Single(AdvancedMeasurementService.EvaluateAll(viewer.ViewerState.AllRois, 0.5, "mm")).Success);
            viewer.ViewerState.UndoRedo.Undo();
            Assert.True(Assert.Single(AdvancedMeasurementService.EvaluateAll(viewer.ViewerState.AllRois, 0.5, "mm")).Success);
        });
    }

    [Fact]
    public void DisposedViewerCannotCreateAWorkbench()
    {
        WpfTestRunner.Run(() =>
        {
            var viewer = new Viewer();
            viewer.Dispose();
            Assert.Throws<ObjectDisposedException>(viewer.ShowAdvancedMeasurements);
        });
    }
}
