using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionStudio.Abstractions;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.ViewModels;
using VisionStudio.Wpf.Views;
using Xunit;

namespace VisionStudio.Wpf.Tests;

[Collection("WPF UI")]
public sealed class WpfWindowSmokeTests
{
    private readonly WpfDispatcherFixture _dispatcher;

    public WpfWindowSmokeTests(WpfDispatcherFixture dispatcher) => _dispatcher = dispatcher;

    [Fact]
    public async Task MainWindow_And_NodeDetailWindow_Instantiate_OnStaThread()
    {
        var tempDataRoot = Path.Combine(Path.GetTempPath(), "visionstudio-wpf-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await InProcessVisionHost.StartNewAsync(tempDataRoot);

            await _dispatcher.RunAsync(async () =>
            {
                var window = new MainWindow(host);
                Assert.NotNull(window.DataContext);

                var vm = (MainWindowViewModel)window.DataContext;
#if USE_EXTERNAL_IMAGE_VIEWER
                var editor = Assert.IsType<WorkflowEditorView>(window.FindName("EditorView"));
                var devices = Assert.IsType<DeviceMonitorView>(window.FindName("DevicesView"));
                Assert.True(editor.ImageViewerAdapter.IsExternalImageViewerActive);
                Assert.True(devices.CameraViewerAdapter.IsExternalImageViewerActive);
#endif
                var firstNode = vm.Editor.Nodes.First();
                var detailVm = new NodeDetailViewModel(firstNode);
                var detailWindow = new NodeDetailWindow(detailVm);
                Assert.NotNull(detailWindow.DataContext);
#if USE_EXTERNAL_IMAGE_VIEWER
                var preview = Assert.IsType<ImageViewerWorkspaceControl>(detailWindow.FindName("NodePreviewViewer"));
                Assert.True(preview.IsExternalImageViewerActive);
                detailVm.NodePreviewImage = CreateImage();
                detailWindow.Show();
                detailWindow.UpdateLayout();
                Assert.Same(detailVm.NodePreviewImage, preview.ImageSource);
#endif
                detailWindow.Close();
                window.Show();
                vm.SelectedTabIndex = 3;
                window.UpdateLayout();
                var diagnosticsView = Assert.IsType<TraceDiagnosticsView>(window.FindName("DiagnosticsView"));
                var detailsBox = Assert.IsType<TextBox>(diagnosticsView.FindName("TraceDetailsBox"));
                var logsGrid = Assert.IsType<DataGrid>(diagnosticsView.FindName("LogsDataGrid"));
                var detailsRect = new Rect(detailsBox.TranslatePoint(new Point(), window), new Size(detailsBox.ActualWidth, detailsBox.ActualHeight));
                var logsRect = new Rect(logsGrid.TranslatePoint(new Point(), window), new Size(logsGrid.ActualWidth, logsGrid.ActualHeight));
                Assert.True(detailsRect.Width > 50 && detailsRect.Height > 50, $"Unexpected TraceDetailsBox bounds: {detailsRect}");
                Assert.True(logsRect.Width > 50 && logsRect.Height > 50, $"Unexpected LogsDataGrid bounds: {logsRect}");
                Assert.False(detailsRect.IntersectsWith(logsRect), $"TraceDetailsBox {detailsRect} overlaps LogsDataGrid {logsRect}");

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.SetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            });
        }
        finally
        {
            try { Directory.Delete(tempDataRoot, recursive: true); } catch { }
        }
    }

#if USE_EXTERNAL_IMAGE_VIEWER
    [Fact]
    public void ImageViewer_DefaultBackend_DisplaysClearsAndRoutesViewportButtons()
    {
        RunOnSta(() =>
        {
            using var control = new ImageViewerWorkspaceControl();
            var native = GetNativeViewer(control);
            var window = new Window { Content = control, Width = 900, Height = 650 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var image = CreateImage();
                control.SetImage(image, "integration", fitToView: false);
                Assert.Same(image, native.ImageSource);
                Assert.Same(image, control.ImageSource);
                Assert.False(native.IsDirty);

                Click(control, "ActualSizeButton");
                Assert.Equal(1, native.Scale, 6);
                Click(control, "ZoomInButton");
                Assert.Equal(1.25, native.Scale, 6);
                Click(control, "ZoomOutButton");
                Assert.Equal(1, native.Scale, 6);
                Click(control, "FitButton");
                Assert.True(native.Scale > 1);
                Click(control, "ActualSizeButton");
                Assert.Equal(1, native.Scale, 6);
                Assert.Throws<ArgumentOutOfRangeException>(() => native.ZoomAtViewportCenter(double.NaN));
                Assert.Throws<ArgumentOutOfRangeException>(() => native.ZoomAtViewportCenter(0));

                control.SetImage(null);
                Assert.Null(native.ImageSource);
                Assert.Null(control.ImageSource);
                control.Dispose();
                control.Dispose();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ImageViewer_BlobRoi_IsConvertedBeforeItsRotatedRectBase()
    {
        RunOnSta(() =>
        {
            using var control = new ImageViewerWorkspaceControl();
            control.ImageSource = CreateImage();
            var native = GetNativeViewer(control);
            Assert.True(native.AddRoi(new global::ImageViewer.Models.BlobAnalysisRoi
            {
                Center = new(4, 4), Width = 4, Height = 2, Angle = 90
            }));
            var roi = Assert.IsType<VisionRoi>(control.TryGetActiveUserRoi());
            Assert.Equal(VisionRoiType.Rectangle, roi.Type);
            Assert.Equal(2, roi.X);
            Assert.Equal(3, roi.Y);
            Assert.Equal(4, roi.Width);
            Assert.Equal(2, roi.Height);
        });
    }

    [Fact]
    public void ImageViewer_MapsAllOverlayTypes_WithoutDuplicatesOrLosingUserRois()
    {
        RunOnSta(() =>
        {
            using var control = new ImageViewerWorkspaceControl();
            var native = GetNativeViewer(control);
            control.ImageSource = CreateImage();
            VisionOverlayPoint[] points = [new(1, 1), new(4, 1), new(4, 4)];
            VisionOverlay[] overlays =
            [
                new("point", VisionOverlayType.Point, X: 2, Y: 3),
                new("line", VisionOverlayType.Line, X: 1, Y: 2, X2: 5, Y2: 6),
                new("circle", VisionOverlayType.Circle, X: 3, Y: 4, Radius: 2),
                new("rectangle", VisionOverlayType.Rectangle, X: 2, Y: 2, Width: 4, Height: 2, AngleDeg: 90),
                new("polygon", VisionOverlayType.Polygon, Points: points),
                new("contour", VisionOverlayType.Contour, Points: points),
                new("polyline", VisionOverlayType.Polyline, Points: points),
                new("text", VisionOverlayType.Text, X: 1, Y: 1, Label: "result"),
                new("region", VisionOverlayType.Region, Runs: [new(1, 1, 3), new(2, 1, 4)])
            ];

            control.SetOverlays(overlays);
            Assert.Equal(10, native.ViewerState.AllRois.Count);
            Assert.All(native.ViewerState.AllRois, roi =>
            {
                Assert.True(roi.IsLocked);
                Assert.True(roi.IsVisible);
            });
            Assert.False(native.ViewerState.UndoRedo.CanUndo);
            Assert.False(native.IsDirty);
            var rectangle = Assert.Single(native.ViewerState.AllRois.OfType<global::ImageViewer.Models.PolygonRoi>(),
                roi => roi.Points.Count == 4);
            Assert.Equal(5, rectangle.Points[0].X, 6);
            Assert.Equal(1, rectangle.Points[0].Y, 6);
            Assert.Equal("result", Assert.Single(native.ViewerState.AllRois.OfType<global::ImageViewer.Models.TextAnnotationRoi>()).Label);
            Assert.Equal(global::ImageViewer.Models.RoiColor.Parse("#52c41a"),
                Assert.Single(native.ViewerState.AllRois.OfType<global::ImageViewer.Models.PointAnnotationRoi>()).StrokeColor);

            control.SetOverlays(overlays);
            Assert.Equal(10, native.ViewerState.AllRois.Count);
            var checkbox = Assert.IsType<CheckBox>(control.FindName("ShowOverlaysCheckBox"));
            checkbox.IsChecked = false;
            Assert.All(native.ViewerState.AllRois, roi => Assert.False(roi.IsVisible));
            checkbox.IsChecked = true;
            Assert.All(native.ViewerState.AllRois, roi => Assert.True(roi.IsVisible));
            Assert.False(native.IsDirty);

            var userRoi = new global::ImageViewer.Models.CircleRoi { Center = new(4, 4), Radius = 1, Label = "user" };
            Assert.True(native.AddRoi(userRoi));
            Assert.True(native.IsDirty);
            control.SetOverlays(overlays);
            Assert.Equal(11, native.ViewerState.AllRois.Count);
            Assert.Contains(userRoi, native.ViewerState.AllRois);
            Assert.Same(userRoi, native.ViewerState.SelectedRoi);
            Assert.True(native.IsDirty);
            control.SetOverlays(null);
            Assert.Same(userRoi, Assert.Single(native.ViewerState.AllRois));
            Assert.Throws<ArgumentException>(() => control.SetOverlays([new("invalid", VisionOverlayType.Circle)]));
        });
    }

    [Fact]
    public void ImageViewer_RegionSampling_IsBounded_AndPreloadedOverlaysBecomeVisible()
    {
        RunOnSta(() =>
        {
            using var control = new ImageViewerWorkspaceControl();
            var native = GetNativeViewer(control);
            var runs = Enumerable.Range(0, 4500).Select(row => new VisionRegionRun(row, 0, 7)).ToArray();
            control.SetOverlays([new("region", VisionOverlayType.Region, Runs: runs)]);
            Assert.InRange(native.ViewerState.AllRois.Count, 1, 2000);
            Assert.All(native.ViewerState.AllRois, roi => Assert.False(roi.IsVisible));
            control.SetImage(CreateImage(), fitToView: false);
            Assert.All(native.ViewerState.AllRois, roi => Assert.True(roi.IsVisible));
            control.SetImage(null);
            Assert.Empty(native.ViewerState.AllRois);
        });
    }

    private static BitmapSource CreateImage()
    {
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Gray8, null, new byte[64], 8);
        image.Freeze();
        return image;
    }

    private static global::ImageViewer.Controls.ImageViewer GetNativeViewer(ImageViewerWorkspaceControl control)
    {
        Assert.True(control.IsExternalImageViewerActive);
        var viewport = Assert.IsType<Grid>(control.FindName("ViewportHostGrid"));
        return Assert.IsType<global::ImageViewer.Controls.ImageViewer>(Assert.Single(viewport.Children.Cast<UIElement>()));
    }

    private static void Click(ImageViewerWorkspaceControl control, string buttonName)
    {
        var button = Assert.IsType<Button>(control.FindName(buttonName));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void RunOnSta(Action action) =>
        _dispatcher.RunAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
#endif
}
