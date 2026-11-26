using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media.Media3D;
using ImageViewer.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class Volume3DViewerTests
    {
        [Fact]
        public void AssigningVolume_CreatesThreeDimensionalScene()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new Volume3DViewer();
                var volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)], spacingX: 0.5, spacingY: 1, spacingZ: 2);

                viewer.Volume = volume;

                Assert.Same(volume, viewer.Volume);
                Assert.Equal(4, viewer.SceneViewport.Items.Count);
                Assert.Equal(0, viewer.CurrentSliceIndex);
                Assert.Equal(1, viewer.CurrentCoronalSliceIndex);
                Assert.Equal(1, viewer.CurrentSagittalSliceIndex);
                viewer.Dispose();
                viewer.Dispose();
            });
        }

        [Fact]
        public void ContextMenu_ProvidesThreeDimensionalCommands()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();
                ContextMenu menu = viewer.threeDContextMenu;

                Assert.Contains(menu.Items.OfType<MenuItem>(), item => (string)item.Header == UiText.Get("Menu3DResetCamera"));
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => (string)item.Header == UiText.Get("Menu3DFitVolume"));
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => (string)item.Header == UiText.Get("Menu3DShowCoordinateSystem"));
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => (string)item.Header == UiText.Get("Menu3DSwitchToAxialSlice"));
            });
        }

        [Fact]
        public void AdaptiveHost_HandlesSwitchToAxialSliceRequest()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)])
                };
                ContextMenu menu = viewer.Volume3DViewer.threeDContextMenu;
                var switchMenuItem = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == UiText.Get("Menu3DSwitchToAxialSlice"));
                switchMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.IsType<VolumeViewer>(viewer.ActiveView);
            });
        }

        [Fact]
        public void CropBounds_AddsCropBoxWithoutChangingVolume()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();
                var volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)]);
                viewer.Volume = volume;

                viewer.CropBounds = new VolumeCropBounds(-2, 0, 1, 8, 0, 1);

                Assert.Same(volume, viewer.Volume);
                Assert.Equal(new VolumeCropBounds(0, 0, 1, 1, 0, 1), viewer.CropBounds);
                Assert.Equal(5, viewer.SceneViewport.Items.Count);
            });
        }

        [Fact]
        public void TryPickVoxelAtWorldPoint_RaisesVoxelPicked()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)], spacingX: 0.5, spacingY: 1, spacingZ: 2)
                };
                VolumeVoxelLocation? picked = null;
                viewer.VoxelPicked += (_, args) => picked = args.Voxel;

                bool found = viewer.TryPickVoxelAtWorldPoint(new Point3D(0.25, 0.5, 1));

                Assert.True(found);
                Assert.NotNull(picked);
                Assert.Equal(1, picked!.X);
                Assert.Equal(1, picked.Y);
                Assert.Equal(1, picked.Z);
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
    }
}
