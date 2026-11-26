using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ImageViewer.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public sealed class Volume3DViewerInteractionTests
    {
        [Fact]
        public void CameraPresets_MoveCameraToVolumeExtents()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)])
                };

                viewer.ShowFrontView();
                AssertCamera(viewer, new Point3D(0, 0, 4.8), new Vector3D(0, 1, 0));

                viewer.ShowBackView();
                AssertCamera(viewer, new Point3D(0, 0, -4.8), new Vector3D(0, 1, 0));

                viewer.ShowTopView();
                AssertCamera(viewer, new Point3D(0, 4.8, 0), new Vector3D(0, 0, -1));

                viewer.ShowBottomView();
                AssertCamera(viewer, new Point3D(0, -4.8, 0), new Vector3D(0, 0, -1));

                viewer.ShowSideView();
                AssertCamera(viewer, new Point3D(4.8, 0, 0), new Vector3D(0, 1, 0));

                viewer.ShowOppositeSideView();
                AssertCamera(viewer, new Point3D(-4.8, 0, 0), new Vector3D(0, 1, 0));

                viewer.ShowIsometricView();
                AssertCamera(viewer, new Point3D(4.8, 4.8, 4.8), new Vector3D(0, 0, -1));

                viewer.FitVolume();
                AssertCamera(viewer, new Point3D(7.68, 6.72, 8.64), new Vector3D(0, 0, -1));

                viewer.ResetCamera();
                AssertCamera(viewer, new Point3D(0, 0, 3), new Vector3D(0, 1, 0));
            });
        }

        [Fact]
        public void InteractionsWithoutVolume_ResetCameraAndReportMissingSlices()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();

                viewer.ShowFrontView();
                AssertCamera(viewer, new Point3D(0, 0, 3), new Vector3D(0, 1, 0));

                viewer.SetCurrentSlice(3);
                viewer.SetCoronalSlice(3);
                viewer.SetSagittalSlice(3);
                Assert.Equal(-1, viewer.CurrentSliceIndex);
                Assert.Equal(-1, viewer.CurrentCoronalSliceIndex);
                Assert.Equal(-1, viewer.CurrentSagittalSliceIndex);

                Assert.False(viewer.TryPickVoxelAtWorldPoint(new Point3D(0, 0, 0)));

                viewer.CropBounds = new VolumeCropBounds(0, 0, 0, 1, 1, 1);
                Assert.Null(viewer.CropBounds);
            });
        }

        [Fact]
        public void SliceSetters_ClampToVolumeDimensions()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14), CreateBitmap(28)])
                };

                viewer.SetCurrentSlice(-5);
                Assert.Equal(0, viewer.CurrentSliceIndex);
                viewer.SetCurrentSlice(42);
                Assert.Equal(2, viewer.CurrentSliceIndex);

                viewer.SetCoronalSlice(42);
                Assert.Equal(1, viewer.CurrentCoronalSliceIndex);
                viewer.SetCoronalSlice(-5);
                Assert.Equal(0, viewer.CurrentCoronalSliceIndex);

                viewer.SetSagittalSlice(-5);
                Assert.Equal(0, viewer.CurrentSagittalSliceIndex);
                viewer.SetSagittalSlice(42);
                Assert.Equal(1, viewer.CurrentSagittalSliceIndex);
            });
        }

        [Fact]
        public void VolumeOpacity_ClampsAndUpdatesAssignedVolume()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();
                viewer.VolumeOpacity = 5;
                Assert.Equal(0.9, viewer.VolumeOpacity, 6);
                viewer.VolumeOpacity = -1;
                Assert.Equal(0.05, viewer.VolumeOpacity, 6);

                viewer.Volume = new VolumeData([CreateBitmap(7)]);
                viewer.VolumeOpacity = 0.3;
                Assert.Equal(0.3, viewer.VolumeOpacity, 6);
            });
        }

        [Fact]
        public void CropBounds_CanBeClearedAndFullBoundsAddNoModel()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();
                var volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)]);
                viewer.Volume = volume;
                int sceneItems = viewer.SceneViewport.Items.Count;

                viewer.CropBounds = VolumeCropBounds.Full(volume);
                Assert.Equal(sceneItems, viewer.SceneViewport.Items.Count);

                viewer.CropBounds = new VolumeCropBounds(-2, 0, 1, 8, 0, 1);
                Assert.Equal(sceneItems + 1, viewer.SceneViewport.Items.Count);

                viewer.CropBounds = null;
                Assert.Equal(VolumeCropBounds.Full(volume), viewer.CropBounds);
                Assert.Equal(sceneItems, viewer.SceneViewport.Items.Count);
            });
        }

        [Fact]
        public void ContextMenuItems_ToggleSceneOptionsAndFireSliceRequest()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)])
                };
                ContextMenu menu = viewer.threeDContextMenu;
                int switchRequests = 0;
                viewer.SwitchToAxialSliceRequested += (_, _) => switchRequests++;

                ClickMenuItem(menu, "Menu3DShowCoordinateSystem");
                Assert.False(viewer.ShowCoordinateSystem);
                ClickMenuItem(menu, "Menu3DShowBoundingBox");
                Assert.False(viewer.ShowBoundingBox);
                ClickMenuItem(menu, "Menu3DShowAxialPlane");
                Assert.False(viewer.ShowAxialPlane);
                ClickMenuItem(menu, "Menu3DShowCoronalPlane");
                Assert.True(viewer.ShowCoronalPlane);
                ClickMenuItem(menu, "Menu3DShowSagittalPlane");
                Assert.True(viewer.ShowSagittalPlane);

                ClickMenuItem(menu, "Menu3DIncreaseTransparency");
                Assert.Equal(0.17, viewer.VolumeOpacity, 6);
                ClickMenuItem(menu, "Menu3DDecreaseTransparency");
                Assert.Equal(0.22, viewer.VolumeOpacity, 6);

                ClickMenuItem(menu, "Menu3DSwitchToAxialSlice");
                Assert.Equal(1, switchRequests);

                ClickMenuItem(menu, "Menu3DFrontView");
                AssertCamera(viewer, new Point3D(0, 0, 4.8), new Vector3D(0, 1, 0));
                ClickMenuItem(menu, "Menu3DResetCamera");
                AssertCamera(viewer, new Point3D(0, 0, 3), new Vector3D(0, 1, 0));
            });
        }

        [Fact]
        public void MenuSearch_FiltersRestoresAndInvokesSingleMatch()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer
                {
                    Volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)])
                };
                ContextMenu menu = viewer.threeDContextMenu;
                var window = new Window { Content = viewer, Width = 320, Height = 240, ShowInTaskbar = false };
                window.Show();
                try
                {
                    PresentationSource windowSource = PresentationSource.FromVisual(window)
                        ?? throw new InvalidOperationException("测试窗口未连接到 PresentationSource。");
                    menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                    WpfTestRunner.DrainDispatcher();

                    var searchBox = (TextBox)menu.Template.FindName("PART_MenuSearchBox", menu);
                    var matchCount = (TextBlock)menu.Template.FindName("PART_MenuSearchMatchCount", menu);
                    var noResults = (TextBlock)menu.Template.FindName("PART_MenuSearchNoResults", menu);
                    Assert.Empty(searchBox.Text);

                    searchBox.Text = "视图";
                    WpfTestRunner.DrainDispatcher();
                    Assert.Equal(Visibility.Visible, matchCount.Visibility);
                    Assert.Equal(UiText.Format("MenuSearchMatchCount", 8), matchCount.Text);
                    Assert.Equal(Visibility.Collapsed, noResults.Visibility);

                    searchBox.Text = "重置";
                    WpfTestRunner.DrainDispatcher();
                    Assert.Equal(UiText.Format("MenuSearchMatchCount", 1), matchCount.Text);
                    viewer.ShowTopView();
                    Assert.True(RaisePreviewKeyDown(searchBox, windowSource, Key.Enter));
                    AssertCamera(viewer, new Point3D(0, 0, 3), new Vector3D(0, 1, 0));

                    searchBox.Text = "正面";
                    WpfTestRunner.DrainDispatcher();
                    Assert.True(RaisePreviewKeyDown(searchBox, windowSource, Key.Down));

                    searchBox.Text = "zzzz-不存在的命令";
                    WpfTestRunner.DrainDispatcher();
                    Assert.Equal(Visibility.Visible, noResults.Visibility);
                    Assert.Equal(Visibility.Collapsed, matchCount.Visibility);

                    searchBox.Text = "视图";
                    WpfTestRunner.DrainDispatcher();
                    Assert.True(RaisePreviewKeyDown(searchBox, windowSource, Key.Escape));
                    Assert.Empty(searchBox.Text);
                    Assert.True(RaisePreviewKeyDown(searchBox, windowSource, Key.Escape));
                    Assert.False(menu.IsOpen);

                    WpfTestRunner.DrainDispatcher();
                    Assert.Equal(Visibility.Collapsed, matchCount.Visibility);
                    Assert.Equal(Visibility.Collapsed, noResults.Visibility);
                    Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Equal(Visibility.Visible, item.Visibility));
                    Assert.All(menu.Items.OfType<Separator>(), separator => Assert.Equal(Visibility.Visible, separator.Visibility));
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        [Fact]
        public void Dispose_GuardsPublicApiAndIsIdempotent()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new Volume3DViewer();
                var volume = new VolumeData([CreateBitmap(7), CreateBitmap(14)]);
                viewer.Volume = volume;

                viewer.Dispose();
                viewer.Dispose();

                Assert.Throws<ObjectDisposedException>(() => viewer.Volume = volume);
                Assert.Throws<ObjectDisposedException>(() => viewer.CropBounds = null);
                Assert.Throws<ObjectDisposedException>(() => viewer.SetCurrentSlice(0));
                Assert.Throws<ObjectDisposedException>(() => viewer.SetCoronalSlice(0));
                Assert.Throws<ObjectDisposedException>(() => viewer.SetSagittalSlice(0));
                Assert.Throws<ObjectDisposedException>(() => viewer.TryPickVoxelAtWorldPoint(new Point3D(0, 0, 0)));
                Assert.Throws<ObjectDisposedException>(() => viewer.ResetCamera());
                Assert.Throws<ObjectDisposedException>(() => viewer.FitVolume());
                Assert.Throws<ObjectDisposedException>(() => viewer.ShowFrontView());
            });
        }

        [Fact]
        public void DisposeAsync_CompletesSynchronouslyAndDisposes()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new Volume3DViewer();

                ValueTask pending = viewer.DisposeAsync();

                Assert.True(pending.IsCompletedSuccessfully);
                Assert.Throws<ObjectDisposedException>(() => viewer.SetCurrentSlice(0));
                viewer.Dispose();
            });
        }

        private static void AssertCamera(Volume3DViewer viewer, Point3D position, Vector3D upDirection)
        {
            var camera = viewer.SceneViewport.Camera;
            Assert.NotNull(camera);
            Assert.Equal(position.X, camera.Position.X, 6);
            Assert.Equal(position.Y, camera.Position.Y, 6);
            Assert.Equal(position.Z, camera.Position.Z, 6);
            Assert.Equal(-position.X, camera.LookDirection.X, 6);
            Assert.Equal(-position.Y, camera.LookDirection.Y, 6);
            Assert.Equal(-position.Z, camera.LookDirection.Z, 6);
            Assert.Equal(upDirection.X, camera.UpDirection.X, 6);
            Assert.Equal(upDirection.Y, camera.UpDirection.Y, 6);
            Assert.Equal(upDirection.Z, camera.UpDirection.Z, 6);
        }

        private static void ClickMenuItem(ContextMenu menu, string headerResourceKey)
        {
            MenuItem item = menu.Items.OfType<MenuItem>()
                .Single(candidate => candidate.Header?.ToString() == UiText.Get(headerResourceKey));
            if (item.IsCheckable)
            {
                item.IsChecked = !item.IsChecked;
            }
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }

        private static bool RaisePreviewKeyDown(UIElement element, PresentationSource source, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            element.RaiseEvent(args);
            return args.Handled;
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