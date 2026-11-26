using System.Linq;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    public class ImageViewerRoiCollectionStateTests
    {
        [Fact]
        public void MutatingTypedCollection_UpdatesAllRoisThroughTheSharedStore()
        {
            WpfTestRunner.Run(() =>
            {
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                var roi = new LineMeasureRoi();

                viewModel.LineMeasureRois.Add(roi);

                Assert.Same(roi, Assert.Single(viewModel.AllRois));

                viewModel.LineMeasureRois.Remove(roi);

                Assert.Empty(viewModel.AllRois);
            });
        }

        [Fact]
        public void ReplaceAllRois_RebuildsOneConsistentStateAndClearsSelection()
        {
            WpfTestRunner.Run(() =>
            {
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                var original = new LineMeasureRoi { Label = "original" };
                var replacement = new CircleRoi { Label = "replacement" };

                viewModel.AddRoi(original);
                viewModel.SelectedRoi = original;

                viewModel.ReplaceAllRois([replacement]);

                Assert.Null(viewModel.SelectedRoi);
                Assert.Same(replacement, Assert.Single(viewModel.AllRois));
                Assert.Empty(viewModel.LineMeasureRois);
                Assert.Same(replacement, Assert.Single(viewModel.CircleRois));
                Assert.Equal([replacement], viewModel.AllRois.ToArray());
            });
        }
    }
}
