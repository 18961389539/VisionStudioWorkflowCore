using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class VolumeViewerTests
    {
        [Fact]
        public void VolumeAndSlider_SelectAxialSlice()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource first = CreateBitmap(10);
                BitmapSource second = CreateBitmap(20);
                var volume = new VolumeData([first, second]);
                using var viewer = new VolumeViewer { Volume = volume };
                Slider slider = viewer.sliceSlider;
                var window = new Window { Content = viewer };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    viewer.SliceViewer.MarkDocumentClean();
                    Assert.Equal(0, viewer.CurrentSliceIndex);
                    Assert.Equal(10, ReadFirstPixel(viewer.SliceViewer.ImageSource));

                    slider.Value = 1;
                    WpfTestRunner.DrainDispatcher();
                    Assert.Equal(1, viewer.CurrentSliceIndex);
                    Assert.Equal(20, ReadFirstPixel(viewer.SliceViewer.ImageSource));
                    Assert.False(viewer.SliceViewer.IsDirty);

                    Assert.True(viewer.SliceViewer.AddRoi(new CircleRoi { Radius = 1 }));
                    viewer.SelectSlice(0);
                    Assert.True(viewer.SliceViewer.IsDirty);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        private static BitmapSource CreateBitmap(byte value)
        {
            return BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Gray8,
                null,
                new[] { value, value, value, value },
                2);
        }

            private static byte ReadFirstPixel(ImageSource? source)
            {
                BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
                byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth, 0);
                return pixels[0];
            }
    }
}
