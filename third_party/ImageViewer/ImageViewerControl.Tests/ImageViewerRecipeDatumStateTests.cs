using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Core.Persistence;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class ImageViewerRecipeDatumStateTests
    {
        [Fact]
        public void ChangingImageSourceClearsBothCapturedDatumPairs()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                viewer.SetRecipeReferenceDatum(0, new RecipeDatumPoint { X = 10, Y = 20 });
                viewer.SetRecipeReferenceDatum(1, new RecipeDatumPoint { X = 30, Y = 40 });
                viewer.SetRecipeCurrentDatum(0, new RecipeDatumPoint { X = 50, Y = 60 });
                viewer.SetRecipeCurrentDatum(1, new RecipeDatumPoint { X = 70, Y = 80 });

                viewer.ImageSource = BitmapSource.Create(
                    1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);

                Assert.Empty(viewer.GetRecipeReferenceDatums());
                Assert.Empty(viewer.GetRecipeCurrentDatums());
            });
        }
    }
}
