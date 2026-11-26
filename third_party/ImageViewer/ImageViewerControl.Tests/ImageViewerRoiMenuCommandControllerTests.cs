using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerRoiMenuCommandControllerTests
    {
        [Fact]
        public void Execute_SetColorCommand_UsesExpectedColorAndRefreshesMenuState()
        {
            var host = new FakeRoiMenuCommandHost();
            var controller = new ImageViewerRoiMenuCommandController(host);

            controller.Execute(ImageViewerRoiMenuCommand.SetColorYellow);

            Assert.Equal(RoiColors.Yellow, host.LastColor);
            Assert.Equal(1, host.UpdateContextMenuStateCount);
        }

        [Fact]
        public void Execute_EditProperties_DelegatesToHost()
        {
            var host = new FakeRoiMenuCommandHost();
            var controller = new ImageViewerRoiMenuCommandController(host);

            controller.Execute(ImageViewerRoiMenuCommand.EditProperties);

            Assert.Equal(1, host.EditSelectedPropertiesCount);
            Assert.Equal(1, host.UpdateContextMenuStateCount);
        }

        [Fact]
        public void ClearAllRoisCommand_Undo_RestoresRoisAndOriginalSelection()
        {
            var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
            var first = new CircleRoi();
            var selected = new RotatedRect();
            viewModel.AddRoi(first);
            viewModel.AddRoi(selected);
            viewModel.SelectedRoi = selected;

            viewModel.UndoRedo.Execute(new ClearAllRoisCommand(viewModel));
            viewModel.UndoRedo.Undo();

            Assert.Equal(2, viewModel.AllRois.Count);
            Assert.Contains(first, viewModel.AllRois);
            Assert.Contains(selected, viewModel.AllRois);
            Assert.Same(selected, viewModel.SelectedRoi);
        }

        private sealed class FakeRoiMenuCommandHost : IImageViewerRoiMenuCommandHost
        {
            public int EditSelectedPropertiesCount { get; private set; }
            public int UpdateContextMenuStateCount { get; private set; }
            public RoiColor? LastColor { get; private set; }

            public void Undo() { }
            public void Redo() { }
            public void DeleteSelected() { }
            public void ClearAll() { }
            public void EditSelectedProperties() => EditSelectedPropertiesCount++;
            public void SetSelectedLabel() { }
            public void SetSelectedColor(RoiColor color) => LastColor = color;
            public void CalibrateSelectedRoi() { }
            public void EditSelectedCaliperSettings() { }
            public void UpdateContextMenuState() => UpdateContextMenuStateCount++;
        }
    }
}