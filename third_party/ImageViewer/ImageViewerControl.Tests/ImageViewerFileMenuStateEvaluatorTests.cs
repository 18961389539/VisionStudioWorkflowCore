using ImageViewer.Controls;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerFileMenuStateEvaluatorTests
    {
        [Fact]
        public void Evaluate_WithoutRecentProjects_UsesDisabledPlaceholder()
        {
            ImageViewerFileMenuState state = ImageViewerFileMenuStateEvaluator.Evaluate(
                new ImageViewerFileMenuStateInput(
                    HasRois: false,
                    HasContent: false,
                    AutoSaveEnabled: true,
                    RecentProjects: System.Array.Empty<ImageViewerDynamicMenuItem>()));

            Assert.False(state.SaveRoisEnabled);
            Assert.False(state.SaveSessionEnabled);
            Assert.True(state.AutoSaveChecked);
            Assert.False(state.RecentProjectsEnabled);
            Assert.Single(state.RecentProjects);
            Assert.False(state.RecentProjects[0].IsEnabled);
        }

        [Fact]
        public void Evaluate_WithContentAndRecentProjects_EnablesFileActions()
        {
            ImageViewerFileMenuState state = ImageViewerFileMenuStateEvaluator.Evaluate(
                new ImageViewerFileMenuStateInput(
                    HasRois: true,
                    HasContent: true,
                    AutoSaveEnabled: false,
                    RecentProjects: new[]
                    {
                        ImageViewerDynamicMenuItem.FromRecentProject(new RecentImageViewerProject("demo", "c:/demo.ivsession", "session", default))
                    }));

            Assert.True(state.SaveRoisEnabled);
            Assert.True(state.SaveSessionEnabled);
            Assert.True(state.ExportProjectPackageEnabled);
            Assert.False(state.AutoSaveChecked);
            Assert.True(state.RecentProjectsEnabled);
            Assert.Single(state.RecentProjects);
        }
    }
}