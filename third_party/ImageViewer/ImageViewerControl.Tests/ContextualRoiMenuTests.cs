using System.Collections.ObjectModel;
using System.Windows;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class ContextualRoiMenuTests
{
    [Fact]
    public void RightClickSelectsHitPathAndCapturesVertexWithoutDeletingOrOpeningProperties()
    {
        WpfTestRunner.Run(() =>
        {
            var path = new PolygonRoi
            {
                IsClosed = true,
                Points = new ObservableCollection<PointD> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) }
            };
            var host = new FakeSelectionHost { Hit = path, PointIndex = 2, SelectedRoi = new LineMeasureRoi() };
            var flow = new SelectionInteractionFlow(host);

            bool handled = flow.HandleRightClick(new Point(10, 10));

            Assert.False(handled);
            Assert.Same(path, host.SelectedRoi);
            Assert.Same(path, host.CapturedRoi);
            Assert.Equal(2, host.CapturedIndex);
            Assert.Equal(new PointD(10, 10), path.Points[2]);
            Assert.Equal(1, host.DrawCount);
            Assert.Null(host.PropertiesRoi);
        });
    }

    [Fact]
    public void BlankRightClickClearsSelectionAndVertexTarget()
    {
        WpfTestRunner.Run(() =>
        {
            var host = new FakeSelectionHost
            {
                SelectedRoi = new LineMeasureRoi(),
                CapturedRoi = new PolylineRoi(),
                CapturedIndex = 1
            };
            var flow = new SelectionInteractionFlow(host);

            bool handled = flow.HandleRightClick(new Point(100, 100));

            Assert.False(handled);
            Assert.Null(host.SelectedRoi);
            Assert.Null(host.CapturedRoi);
            Assert.Null(host.CapturedIndex);
            Assert.Equal(1, host.DrawCount);
        });
    }

    [Fact]
    public void ExplicitVertexDeleteIsUndoableAndRejectsStaleOrMinimumGeometry()
    {
        WpfTestRunner.Run(() =>
        {
            var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
            var path = new PolylineRoi
            {
                Points = new ObservableCollection<PointD> { new(0, 0), new(4, 1), new(8, 0) }
            };
            Assert.True(viewModel.AddRoi(path));
            viewModel.SelectedRoi = path;
            PointD[] original = path.Points.ToArray();
            var target = new ContextualPathVertexTarget(path, path.Points, 1, path.Points[1], path.Points.ToArray());

            Assert.True(ContextualPathVertexEdit.TryDelete(target, path, viewModel.AllRois, viewModel, out string reason));
            Assert.Equal(new[] { original[0], original[2] }, path.Points);
            Assert.True(viewModel.UndoRedo.CanUndo);

            viewModel.UndoRedo.Undo();
            Assert.Equal(original, path.Points);
            Assert.False(ContextualPathVertexEdit.CanDelete(target, path, viewModel.AllRois, out reason));
            Assert.Equal("ContextRoiVertexStale", reason);

            viewModel.UndoRedo.Redo();
            Assert.False(ContextualPathVertexEdit.CanDelete(target, path, viewModel.AllRois, out reason));
            Assert.Equal("ContextRoiVertexStale", reason);

            var minimumPath = new PolylineRoi { Points = new ObservableCollection<PointD> { new(0, 0), new(1, 0) } };
            Assert.True(viewModel.AddRoi(minimumPath));
            var minimumTarget = new ContextualPathVertexTarget(minimumPath, minimumPath.Points, 0, minimumPath.Points[0], minimumPath.Points.ToArray());
            Assert.False(ContextualPathVertexEdit.CanDelete(minimumTarget, minimumPath, viewModel.AllRois, out reason));
            Assert.Equal("ContextRoiVertexMinimum", reason);
        });
    }

    private sealed class FakeSelectionHost : IImageViewerSelectionInteractionHost
    {
        public bool IsToolInteractionActive { get; set; }
        public RoiBase? SelectedRoi { get; set; }
        public RoiBase? Hit { get; set; }
        public int PointIndex { get; set; } = -1;
        public RoiBase? CapturedRoi { get; set; }
        public int? CapturedIndex { get; set; }
        public RoiBase? PropertiesRoi { get; private set; }
        public int DrawCount { get; private set; }
        public RoiBase? HitTest(Point point) => Hit;
        public int GetPathVertexIndexAt(Point point) => PointIndex;
        public void CaptureContextualPathVertex(RoiBase? roi, int? pointIndex)
        {
            CapturedRoi = roi;
            CapturedIndex = pointIndex;
        }
        public void ExitCurrentMode() { }
        public void DrawRois() => DrawCount++;
        public void ShowRoiProperties(RoiBase roi) => PropertiesRoi = roi;
    }
}
