using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;
using ImageViewerControl2D = ImageViewer.Controls.ImageViewer;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class AdaptiveImageViewerTests
    {
        [Fact]
        public void AutoMode_SelectsTwoDimensionalViewForImage()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer();
                viewer.ImageSource = CreateBitmap(9);

                Assert.IsType<ImageViewerControl2D>(viewer.ActiveView);
            });
        }

        [Fact]
        public void AutoMode_SelectsThreeDimensionalViewForVolume()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)])
                };

                Assert.IsType<Volume3DViewer>(viewer.ActiveView);
            });
        }

        [Fact]
        public void ReplaceVolumeDocument_ProtectsAndClearsBothViewers()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                var previous = new VolumeData([CreateBitmap(9), CreateBitmap(18)]);
                var replacement = new VolumeData([CreateBitmap(27), CreateBitmap(36)]);
                using var viewer = new AdaptiveImageViewer { Volume = previous };
                Assert.True(viewer.ImageViewer.AddRoi(new CircleRoi { Radius = 1 }));
                viewer.ImageViewer.MarkDocumentClean();
                ImageViewerControl2D axial = viewer.VolumeViewer.SliceViewer;
                Assert.True(axial.AddRoi(new CircleRoi { Radius = 1 }));
                axial.PixelSize = 0.5;
                axial.PhysicalUnit = "mm";

                Assert.False(await viewer.ReplaceVolumeDocumentAsync(replacement));
                Assert.Same(previous, viewer.Volume);
                Assert.Single(viewer.ImageViewer.ViewerState.AllRois);
                Assert.Single(axial.ViewerState.AllRois);
                Assert.True(axial.IsDirty);

                axial.MarkDocumentClean();
                Assert.True(await viewer.ReplaceVolumeDocumentAsync(replacement));
                Assert.Same(replacement, viewer.Volume);
                Assert.Empty(viewer.ImageViewer.ViewerState.AllRois);
                Assert.Empty(axial.ViewerState.AllRois);
                Assert.Equal(1, axial.PixelSize);
                Assert.Equal("px", axial.PhysicalUnit);
                Assert.False(axial.IsDirty);
                Assert.True(viewer.IsDirty);

                viewer.ImageViewer.MarkDocumentClean();
                Assert.True(axial.AddRoi(new CircleRoi { Radius = 1 }));
                string sessionPath = Path.Combine(Path.GetTempPath(), $"adaptive-project-{Guid.NewGuid():N}.ivsession");
                try
                {
                    await File.WriteAllTextAsync(sessionPath, """{"Version":2,"Scale":1}""");
                    Assert.False(await viewer.OpenProjectFromFileAsync(sessionPath));
                    Assert.Same(replacement, viewer.Volume);
                    Assert.Single(axial.ViewerState.AllRois);

                    axial.MarkDocumentClean();
                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));
                    Assert.Null(viewer.Volume);
                    Assert.Null(viewer.ImageViewer.ImageSource);
                    Assert.Empty(axial.ViewerState.AllRois);
                    Assert.False(viewer.IsDirty);
                }
                finally
                {
                    File.Delete(sessionPath);
                }
            });
        }

        [Fact]
        public void ExplicitMode_CanSwitchVolumeBetween3DAndAxialSlice()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)]),
                    DisplayMode = AdaptiveDisplayMode.AxialSlice
                };

                Assert.IsType<VolumeViewer>(viewer.ActiveView);
                viewer.DisplayMode = AdaptiveDisplayMode.ThreeDimensional;
                Assert.IsType<Volume3DViewer>(viewer.ActiveView);
            });
        }

        [Fact]
        public void SelectingAxialSlice_SynchronizesThreeDimensionalPlane()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18), CreateBitmap(27)]),
                    DisplayMode = AdaptiveDisplayMode.AxialSlice
                };

                viewer.VolumeViewer.SelectSlice(2);

                Assert.Equal(2, viewer.Volume3DViewer.CurrentSliceIndex);
            });
        }

        [Fact]
        public void SteppingCoronalSlice_UpdatesThreeDimensionalPlane()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18), CreateBitmap(27)])
                };

                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));
                Assert.True(await viewer.StepMprSliceAsync(1));

                Assert.Equal(1, viewer.Volume3DViewer.CurrentCoronalSliceIndex);
            });
        }

        [Fact]
        public void SteppingSagittalSlice_UpdatesThreeDimensionalPlane()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18), CreateBitmap(27)])
                };

                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal));
                Assert.True(await viewer.StepMprSliceAsync(1));

                Assert.Equal(1, viewer.Volume3DViewer.CurrentSagittalSliceIndex);
            });
        }

        [Fact]
        public void VolumeControls_OnlyTakeSpaceWhileVolumeIsLoaded()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer { ImageSource = CreateBitmap(9) };
                viewer.DisplayMode = AdaptiveDisplayMode.ThreeDimensional;
                Assert.Equal(Visibility.Collapsed, viewer.volumeControls.Visibility);
                Assert.Same(viewer.ImageViewer, viewer.contentHost.Content);

                viewer.Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)]);
                Assert.Equal(Visibility.Visible, viewer.volumeControls.Visibility);
                Assert.Same(viewer.Volume3DViewer, viewer.contentHost.Content);

                viewer.Volume = null;
                Assert.Equal(Visibility.Collapsed, viewer.volumeControls.Visibility);
                Assert.Same(viewer.ImageViewer, viewer.contentHost.Content);
            });
        }

        [Fact]
        public void GestureHint_UsesModeSpecificConventionsFor2D_MprAnd3D()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    ImageSource = CreateBitmap(9),
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)])
                };

                viewer.DisplayMode = AdaptiveDisplayMode.TwoDimensional;
                Assert.Equal(UiText.Get("AdaptiveGestureImage"), viewer.gestureHintText.Text);

                viewer.DisplayMode = AdaptiveDisplayMode.Coronal;
                Assert.Equal(UiText.Get("AdaptiveGestureVolume"), viewer.gestureHintText.Text);

                viewer.DisplayMode = AdaptiveDisplayMode.ThreeDimensional;
                Assert.Equal(UiText.Get("AdaptiveGesture3D"), viewer.gestureHintText.Text);
            });
        }

        [Fact]
        public void NestedImageViewer_EscapeCancelsDrawingBeforeAdaptiveHandler()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer();
                var window = new Window
                {
                    Width = 320,
                    Height = 240,
                    Content = viewer
                };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();

                    var tool = viewer.ImageViewer.AvailableDrawingTools[0];
                    tool.Activate(viewer.ImageViewer);
                    Assert.False(string.IsNullOrWhiteSpace(viewer.ImageViewer.ActiveToolName));

                    viewer.ImageViewer.Focus();
                    RaiseKeyDown(viewer.ImageViewer, Key.Escape);
                    WpfTestRunner.DrainDispatcher();

                    Assert.True(string.IsNullOrEmpty(viewer.ImageViewer.ActiveToolName));
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        [Fact]
        public void NestedImageViewer_ArrowKeyMovesSelectedRoiInMprView()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)])
                };
                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));

                var roi = new LineMeasureRoi
                {
                    P1 = new PointD(2, 3),
                    P2 = new PointD(5, 3)
                };
                Assert.True(viewer.ImageViewer.AddRoi(roi));

                var window = new Window
                {
                    Width = 320,
                    Height = 240,
                    Content = viewer
                };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    viewer.ImageViewer.Focus();

                    RaiseKeyDown(viewer.ImageViewer, Key.Right);
                    WpfTestRunner.DrainDispatcher();

                    Assert.Equal(3, roi.P1.X);
                    Assert.Equal(6, roi.P2.X);
                    Assert.Equal(AdaptiveDisplayMode.Coronal, viewer.DisplayMode);
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Mpr_InterruptedRebuildKeepsLastCommittedView(bool failReconstruction)
        {
            WpfTestRunner.RunAsync(async () =>
            {
                var volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)]);
                using var viewer = new AdaptiveImageViewer { Volume = volume };
                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));
                var committedImage = viewer.ImageViewer.ImageSource;
                int committedCoronal = viewer.Volume3DViewer.CurrentCoronalSliceIndex;
                int committedSagittal = viewer.Volume3DViewer.CurrentSagittalSliceIndex;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TimeSpan timeout = TimeSpan.FromSeconds(10);
                try
                {
                    VolumeSliceService.SliceBuildFaultForTest = (current, key) =>
                    {
                        if (!ReferenceEquals(current, volume) || key.Orientation != VolumeSliceOrientation.Sagittal)
                        {
                            return null;
                        }
                        entered.TrySetResult();
                        release.Task.WaitAsync(timeout).GetAwaiter().GetResult();
                        return failReconstruction ? new InvalidOperationException("Test reconstruction failure") : null;
                    };

                    Task<bool> pending = viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal);
                    await entered.Task.WaitAsync(timeout);
                    Assert.False(viewer.ImageViewer.IsEnabled);
                    Assert.True(viewer.cancelButton.IsEnabled);
                    Assert.Same(committedImage, viewer.ImageViewer.ImageSource);
                    if (failReconstruction)
                    {
                        release.SetResult();
                    }
                    else
                    {
                        viewer.cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }

                    Assert.False(await pending.WaitAsync(timeout));
                    Assert.Equal(AdaptiveDisplayMode.Coronal, viewer.DisplayMode);
                    Assert.Same(committedImage, viewer.ImageViewer.ImageSource);
                    Assert.Equal(committedCoronal, viewer.Volume3DViewer.CurrentCoronalSliceIndex);
                    Assert.Equal(committedSagittal, viewer.Volume3DViewer.CurrentSagittalSliceIndex);
                    Assert.True(viewer.ImageViewer.IsEnabled);
                    Assert.False(viewer.cancelButton.IsEnabled);
                }
                finally
                {
                    VolumeSliceService.SliceBuildFaultForTest = null;
                    release.TrySetResult();
                }
            });
        }

        [Fact]
        public void Mpr_CompletesOnOwnerDispatcherAndSwitchingTo2DRestoresStandaloneImage()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource original = CreateBitmap(7);
                using var viewer = new AdaptiveImageViewer
                {
                    ImageSource = original,
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)])
                };

                SynchronizationContext? context = SynchronizationContext.Current;
                Task<bool> pending;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    pending = viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                }

                Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                var slice = Assert.IsAssignableFrom<BitmapSource>(viewer.ImageViewer.ImageSource);
                Assert.NotSame(original, slice);
                BitmapSource replacement = CreateBitmap(23);
                viewer.ImageSource = replacement;
                Assert.Same(slice, viewer.ImageViewer.ImageSource);

                viewer.DisplayMode = AdaptiveDisplayMode.TwoDimensional;
                Assert.Same(replacement, viewer.ImageViewer.ImageSource);
            });
        }

        [Fact]
        public void VolumeSliceStatus_UsesActualHorizontalAndVerticalAxisSpacing()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)], spacingX: 0.5, spacingY: 0.75, spacingZ: 3)
                };

                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.AxialSlice));
                string axial = UiText.Format("StatusAxialSlice", 1, 2);
                Assert.Equal(UiText.Format("StatusVolumePlaneSpacing", axial, "X", 0.5, "Y", 0.75), viewer.statusText.Text);

                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));
                string coronal = UiText.Format("StatusCoronalSlice", 2, 2);
                Assert.Equal(UiText.Format("StatusVolumePlaneSpacing", coronal, "X", 0.5, "Z", 3), viewer.statusText.Text);

                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal));
                viewer.VolumeViewer.SelectSlice(1);
                string sagittal = UiText.Format("StatusSagittalSlice", 2, 2);
                Assert.Equal(UiText.Format("StatusVolumePlaneSpacing", sagittal, "Z", 3, "Y", 0.75), viewer.statusText.Text);
            });
        }

        [Fact]
        public void Mpr_SliderBurstCommitsOnlyTheFinalSlice()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                BitmapSource source = BitmapSource.Create(2, 5, 96, 96, PixelFormats.Gray8, null,
                    new byte[] { 1, 1, 2, 2, 3, 3, 4, 4, 5, 5 }, 2);
                var volume = new VolumeData([source, source]);
                using var viewer = new AdaptiveImageViewer { Volume = volume };
                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));
                int builds = VolumeSliceService.GetSliceBuildCount(volume);

                viewer.mprSliceSlider.Value = 0;
                viewer.mprSliceSlider.Value = 1;
                viewer.mprSliceSlider.Value = 4;

                Assert.Equal(2, viewer.Volume3DViewer.CurrentCoronalSliceIndex);
                Assert.False(viewer.ImageViewer.IsEnabled);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!viewer.ImageViewer.IsEnabled)
                {
                    await Task.Delay(10, timeout.Token);
                }

                Assert.Equal(4, viewer.Volume3DViewer.CurrentCoronalSliceIndex);
                Assert.Equal(builds + 1, VolumeSliceService.GetSliceBuildCount(volume));
                var slice = Assert.IsAssignableFrom<BitmapSource>(viewer.ImageViewer.ImageSource);
                byte[] pixels = new byte[4];
                slice.CopyPixels(pixels, 2, 0);
                Assert.Equal(new byte[] { 5, 5, 5, 5 }, pixels);
            });
        }

        [Fact]
        public void AdaptiveShortcuts_DoNotRunWhileTextInputHasFocus()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([CreateBitmap(9), CreateBitmap(18)])
                };
                viewer.DisplayMode = AdaptiveDisplayMode.ThreeDimensional;

                var editor = new TextBox();
                var host = new Grid();
                host.Children.Add(viewer);
                host.Children.Add(editor);
                var window = new Window
                {
                    Width = 320,
                    Height = 240,
                    Content = host
                };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    editor.Focus();

                    KeyEventArgs args = RaiseKeyDown(viewer, Key.D1);

                    Assert.False(args.Handled);
                    Assert.Equal(AdaptiveDisplayMode.ThreeDimensional, viewer.DisplayMode);
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
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

        private static KeyEventArgs RaiseKeyDown(FrameworkElement source, Key key)
        {
            var args = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(source),
                0,
                key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            source.RaiseEvent(args);
            return args;
        }
    }
}
