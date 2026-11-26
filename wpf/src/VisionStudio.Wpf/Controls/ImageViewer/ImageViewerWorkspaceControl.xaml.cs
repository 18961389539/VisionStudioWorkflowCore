using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VisionStudio.Abstractions;

namespace VisionStudio.Wpf.Controls.ImageViewer;

/// <summary>
/// 图像浏览工作区控件：
/// 1. 遵循 <see cref="IVisionImageViewerAdapter"/> 契约，在编译符号 <c>USE_EXTERNAL_IMAGE_VIEWER</c> 启用时
///    通过 <c>ImageViewerHost</c> 与公共宿主 API 托管 <c>18961389539/ImageViewer</c>；
/// 2. 在未启用外部控件或离线环境下提供内建缩放/平移/像素探针与全量 <see cref="VisionOverlay"/> 矢量叠加渲染。
/// </summary>
public partial class ImageViewerWorkspaceControl : UserControl, IVisionImageViewerAdapter, IDisposable
{
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource), typeof(BitmapSource), typeof(ImageViewerWorkspaceControl),
        new PropertyMetadata(null, OnImageSourceChanged));

    private BitmapSource? _currentImage;
    private IReadOnlyList<VisionOverlay> _overlays = [];
    private Point _lastDragPoint;
    private bool _isDragging;
    private bool _disposed;
    private bool _fitOnLoad;

#if USE_EXTERNAL_IMAGE_VIEWER
    private global::ImageViewer.Controls.ImageViewerHost? _externalHost;
    private global::ImageViewer.Controls.ImageViewer? _externalViewer;
    private List<global::ImageViewer.Models.RoiBase> _externalOverlayRois = [];
#endif

    public BitmapSource? ImageSource
    {
        get => (BitmapSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    private static void OnImageSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ImageViewerWorkspaceControl)sender;
        if (!ReferenceEquals(control._currentImage, args.NewValue))
        {
            control.SetImage((BitmapSource?)args.NewValue);
        }
    }

    public ImageViewerWorkspaceControl()
    {
        InitializeComponent();
        InitializeViewerBackend();
        Loaded += (_, _) =>
        {
            if (_fitOnLoad)
            {
                _fitOnLoad = false;
                FitToView();
            }
        };
        SizeChanged += (_, _) =>
        {
            if (_currentImage is not null && !IsExternalImageViewerActive && Math.Abs(PanTransform.X) < 0.5 && Math.Abs(PanTransform.Y) < 0.5)
            {
                FitToView();
            }
        };
    }

    public bool IsExternalImageViewerActive
    {
        get
        {
#if USE_EXTERNAL_IMAGE_VIEWER
            return _externalViewer is not null;
#else
            return false;
#endif
        }
    }

    private void InitializeViewerBackend()
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        // 严格遵循 18961389539/ImageViewer 宿主生命周期契约：由宿主持有并在控件释放时统一归还租约
        _externalHost = global::ImageViewer.Controls.ImageViewerHost.CreateDefault();
        _externalViewer = _externalHost.CreateViewer();
        ViewportHostGrid.Children.Clear();
        ViewportHostGrid.Children.Add(_externalViewer);
        ZoomText.SetBinding(TextBlock.TextProperty, new Binding(nameof(_externalViewer.Scale))
        {
            Source = _externalViewer,
            StringFormat = "{0:P0}"
        });
        PixelInfoText.Visibility = Visibility.Collapsed;
        ViewerEngineBadge.Text = "图像引擎: 18961389539/ImageViewer";
#else
        ViewerEngineBadge.Text = "图像引擎: 内建高性能渲染（已预留 18961389539/ImageViewer 接入点）";
#endif
    }

    public void SetImage(BitmapSource? image, string? caption = null, bool fitToView = true)
    {
        _currentImage = image;
        _fitOnLoad = image is not null && fitToView && !IsLoaded;
        SetCurrentValue(ImageSourceProperty, image);
        ImageCaptionText.Text = image is null
            ? (caption ?? "未加载图像")
            : $"{caption ?? "图像"}  ({image.PixelWidth} × {image.PixelHeight})";

#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            var wasDirty = _externalViewer.IsDirty;
            _externalViewer.ImageSource = image;
            if (image is not null)
            {
                UpdateExternalOverlayVisibility();
                if (fitToView)
                {
                    _externalViewer.FitImageToView();
                }
            }
            else
            {
                SetOverlays(null);
            }
            if (!wasDirty)
            {
                _externalViewer.MarkDocumentClean();
            }
            return;
        }
#endif

        DisplayImage.Source = image;
        EmptyPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;

        if (image is not null)
        {
            OverlayCanvas.Width = image.PixelWidth;
            OverlayCanvas.Height = image.PixelHeight;
            DisplayImage.Width = image.PixelWidth;
            DisplayImage.Height = image.PixelHeight;
            if (fitToView)
            {
                Dispatcher.BeginInvoke(FitToView);
            }
        }
        else
        {
            OverlayCanvas.Children.Clear();
            PixelInfoText.Text = "-";
            ZoomText.Text = "100%";
        }
    }

    public void SetOverlays(IReadOnlyList<VisionOverlay>? overlays)
    {
        _overlays = overlays ?? [];
        OverlayCountText.Text = $"标注: {_overlays.Count}";

#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            SyncOverlaysToExternalViewer(_overlays);
            return;
        }
#endif

        RenderBuiltinOverlays();
    }

    public void FitToView()
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            _externalViewer.FitImageToView();
            return;
        }
#endif
        if (_currentImage is null || ViewportHostGrid.ActualWidth <= 10 || ViewportHostGrid.ActualHeight <= 10)
        {
            return;
        }

        var scaleX = (ViewportHostGrid.ActualWidth - 24) / _currentImage.PixelWidth;
        var scaleY = (ViewportHostGrid.ActualHeight - 24) / _currentImage.PixelHeight;
        var scale = Math.Clamp(Math.Min(scaleX, scaleY), 0.05, 32.0);

        ZoomTransform.ScaleX = scale;
        ZoomTransform.ScaleY = scale;
        PanTransform.X = (ViewportHostGrid.ActualWidth - _currentImage.PixelWidth * scale) / 2.0;
        PanTransform.Y = (ViewportHostGrid.ActualHeight - _currentImage.PixelHeight * scale) / 2.0;
        UpdateZoomText();
    }

    public void ResetActualSize()
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            _externalViewer.ResetActualSize();
            return;
        }
#endif
        if (_currentImage is null)
        {
            return;
        }

        ZoomTransform.ScaleX = 1.0;
        ZoomTransform.ScaleY = 1.0;
        PanTransform.X = Math.Max(0, (ViewportHostGrid.ActualWidth - _currentImage.PixelWidth) / 2.0);
        PanTransform.Y = Math.Max(0, (ViewportHostGrid.ActualHeight - _currentImage.PixelHeight) / 2.0);
        UpdateZoomText();
    }

    private void RenderBuiltinOverlays()
    {
        if (OverlayCanvas is null)
        {
            return;
        }
        OverlayCanvas.Children.Clear();
        if (ShowOverlaysCheckBox?.IsChecked != true || _overlays.Count == 0)
        {
            return;
        }

        foreach (var overlay in _overlays)
        {
            var strokeBrush = ParseBrush(overlay.Stroke, Brushes.LimeGreen);
            var fillBrush = string.IsNullOrWhiteSpace(overlay.Fill) ? Brushes.Transparent : ParseBrush(overlay.Fill, Brushes.Transparent);
            var strokeWidth = Math.Max(1.0, overlay.StrokeWidth);

            switch (overlay.Type)
            {
                case VisionOverlayType.Point when overlay.X is { } px && overlay.Y is { } py:
                    DrawCrosshair(px, py, strokeBrush, strokeWidth, overlay.Label);
                    break;

                case VisionOverlayType.Line when overlay.X is { } x1 && overlay.Y is { } y1 && overlay.X2 is { } x2 && overlay.Y2 is { } y2:
                    var line = new Line
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Stroke = strokeBrush,
                        StrokeThickness = strokeWidth
                    };
                    OverlayCanvas.Children.Add(line);
                    AddOverlayLabel(overlay.Label, (x1 + x2) / 2, (y1 + y2) / 2, strokeBrush);
                    break;

                case VisionOverlayType.Circle when overlay.X is { } cx && overlay.Y is { } cy && overlay.Radius is { } r:
                    var ellipse = new Ellipse
                    {
                        Width = r * 2,
                        Height = r * 2,
                        Stroke = strokeBrush,
                        StrokeThickness = strokeWidth,
                        Fill = fillBrush
                    };
                    Canvas.SetLeft(ellipse, cx - r);
                    Canvas.SetTop(ellipse, cy - r);
                    OverlayCanvas.Children.Add(ellipse);
                    AddOverlayLabel(overlay.Label, cx - r, cy - r - 18, strokeBrush);
                    break;

                case VisionOverlayType.Rectangle when overlay.X is { } rx && overlay.Y is { } ry && overlay.Width is { } rw && overlay.Height is { } rh:
                    var rect = new Rectangle
                    {
                        Width = Math.Max(1, rw),
                        Height = Math.Max(1, rh),
                        Stroke = strokeBrush,
                        StrokeThickness = strokeWidth,
                        Fill = fillBrush
                    };
                    if (overlay.AngleDeg is { } angle && Math.Abs(angle) > 0.01)
                    {
                        rect.RenderTransformOrigin = new Point(0.5, 0.5);
                        rect.RenderTransform = new RotateTransform(angle);
                    }
                    Canvas.SetLeft(rect, rx);
                    Canvas.SetTop(rect, ry);
                    OverlayCanvas.Children.Add(rect);
                    AddOverlayLabel(overlay.Label, rx, ry - 18, strokeBrush);
                    break;

                case VisionOverlayType.Polygon or VisionOverlayType.Contour or VisionOverlayType.Polyline when overlay.Points is { Count: > 1 } pts:
                    var collection = new PointCollection(pts.Select(p => new Point(p.X, p.Y)));
                    if (overlay.Type == VisionOverlayType.Polyline)
                    {
                        var polyline = new Polyline
                        {
                            Points = collection,
                            Stroke = strokeBrush,
                            StrokeThickness = strokeWidth
                        };
                        OverlayCanvas.Children.Add(polyline);
                    }
                    else
                    {
                        var polygon = new Polygon
                        {
                            Points = collection,
                            Stroke = strokeBrush,
                            StrokeThickness = strokeWidth,
                            Fill = fillBrush
                        };
                        OverlayCanvas.Children.Add(polygon);
                    }
                    AddOverlayLabel(overlay.Label, pts[0].X, pts[0].Y - 18, strokeBrush);
                    break;

                case VisionOverlayType.Text when overlay.X is { } tx && overlay.Y is { } ty:
                    AddOverlayLabel(overlay.Label, tx, ty, strokeBrush);
                    break;

                case VisionOverlayType.Region when overlay.Runs is { Count: > 0 } runs:
                    // 像素游程区域采样绘制（每条游程一条水平线段），避免超大区域生成过多 UI 元素
                    var step = Math.Max(1, (int)Math.Ceiling(runs.Count / 2000.0));
                    for (var i = 0; i < runs.Count; i += step)
                    {
                        var run = runs[i];
                        var span = new Line
                        {
                            X1 = run.StartColumn,
                            Y1 = run.Row,
                            X2 = run.EndColumn,
                            Y2 = run.Row,
                            Stroke = strokeBrush,
                            StrokeThickness = 1,
                            Opacity = 0.55
                        };
                        OverlayCanvas.Children.Add(span);
                    }
                    break;
            }
        }
    }

    private void DrawCrosshair(double x, double y, Brush brush, double strokeWidth, string? label)
    {
        const double arm = 6.0;
        OverlayCanvas.Children.Add(new Line { X1 = x - arm, Y1 = y, X2 = x + arm, Y2 = y, Stroke = brush, StrokeThickness = strokeWidth });
        OverlayCanvas.Children.Add(new Line { X1 = x, Y1 = y - arm, X2 = x, Y2 = y + arm, Stroke = brush, StrokeThickness = strokeWidth });
        AddOverlayLabel(label, x + 8, y - 10, brush);
    }

    private void AddOverlayLabel(string? text, double x, double y, Brush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 15, 23, 42)),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
        Canvas.SetLeft(badge, x);
        Canvas.SetTop(badge, y);
        OverlayCanvas.Children.Add(badge);
    }

    private static Brush ParseBrush(string? colorHex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return fallback;
        }
        try
        {
            if (ColorConverter.ConvertFromString(colorHex) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }
        return fallback;
    }

#if USE_EXTERNAL_IMAGE_VIEWER
    private void SyncOverlaysToExternalViewer(IReadOnlyList<VisionOverlay> overlays)
    {
        if (_externalViewer is null)
        {
            return;
        }

        var replacement = overlays.SelectMany(ConvertOverlayToExternalRois).ToList();
        var retained = _externalViewer.ViewerState.AllRois.Except(_externalOverlayRois).ToArray();
        var selected = _externalViewer.ViewerState.SelectedRoi;
        var wasDirty = _externalViewer.IsDirty;
        _externalViewer.SetRois(retained.Concat(replacement));
        _externalOverlayRois = replacement;
        if (selected is not null && retained.Contains(selected))
        {
            _externalViewer.ViewerState.SelectedRoi = selected;
        }
        if (!wasDirty)
        {
            _externalViewer.MarkDocumentClean();
        }
    }

    private IEnumerable<global::ImageViewer.Models.RoiBase> ConvertOverlayToExternalRois(VisionOverlay overlay)
    {
        var color = global::ImageViewer.Models.RoiColor.Parse(overlay.Stroke)
            ?? throw new ArgumentException($"标注 {overlay.Id} 的颜色无效：{overlay.Stroke}", nameof(overlay));
        var rois = new List<global::ImageViewer.Models.RoiBase>();
        switch (overlay.Type)
        {
            case VisionOverlayType.Point when overlay.X is { } px && overlay.Y is { } py:
                rois.Add(new global::ImageViewer.Models.PointAnnotationRoi { Position = new(px, py) });
                break;
            case VisionOverlayType.Line when overlay.X is { } x1 && overlay.Y is { } y1 && overlay.X2 is { } x2 && overlay.Y2 is { } y2:
                rois.Add(new global::ImageViewer.Models.PolylineRoi { Points = [new(x1, y1), new(x2, y2)] });
                break;
            case VisionOverlayType.Circle when overlay.X is { } cx && overlay.Y is { } cy && overlay.Radius is { } radius:
                rois.Add(new global::ImageViewer.Models.CircleRoi { Center = new(cx, cy), Radius = radius });
                break;
            case VisionOverlayType.Rectangle when overlay.X is { } rx && overlay.Y is { } ry && overlay.Width is { } rw && overlay.Height is { } rh:
                var width = Math.Max(1, rw);
                var height = Math.Max(1, rh);
                var rotation = new RotateTransform(overlay.AngleDeg ?? 0, rx + width / 2, ry + height / 2);
                var corners = new[] { new Point(rx, ry), new Point(rx + width, ry), new Point(rx + width, ry + height), new Point(rx, ry + height) };
                rois.Add(new global::ImageViewer.Models.PolygonRoi
                {
                    IsClosed = true,
                    Points = new ObservableCollection<global::ImageViewer.Models.PointD>(corners.Select(point =>
                    {
                        var rotated = rotation.Transform(point);
                        return new global::ImageViewer.Models.PointD(rotated.X, rotated.Y);
                    }))
                });
                break;
            case VisionOverlayType.Polygon or VisionOverlayType.Contour when overlay.Points is { Count: > 1 } polygon:
                rois.Add(new global::ImageViewer.Models.PolygonRoi
                {
                    IsClosed = true,
                    Points = new ObservableCollection<global::ImageViewer.Models.PointD>(polygon.Select(point => new global::ImageViewer.Models.PointD(point.X, point.Y)))
                });
                break;
            case VisionOverlayType.Polyline when overlay.Points is { Count: > 1 } polyline:
                rois.Add(new global::ImageViewer.Models.PolylineRoi
                {
                    Points = new ObservableCollection<global::ImageViewer.Models.PointD>(polyline.Select(point => new global::ImageViewer.Models.PointD(point.X, point.Y)))
                });
                break;
            case VisionOverlayType.Text when overlay.X is { } tx && overlay.Y is { } ty:
                rois.Add(new global::ImageViewer.Models.TextAnnotationRoi { Position = new(tx, ty) });
                break;
            case VisionOverlayType.Region when overlay.Runs is { Count: > 0 } runs:
                var step = Math.Max(1, (int)Math.Ceiling(runs.Count / 2000.0));
                for (var i = 0; i < runs.Count; i += step)
                {
                    var run = runs[i];
                    rois.Add(new global::ImageViewer.Models.PolylineRoi { Points = [new(run.StartColumn, run.Row), new(run.EndColumn, run.Row)] });
                }
                color = color with { A = (byte)Math.Round(color.A * 0.55) };
                break;
            default:
                throw new ArgumentException($"标注 {overlay.Id} 缺少 {overlay.Type} 所需的几何数据。", nameof(overlay));
        }

        foreach (var roi in rois)
        {
            roi.Label = overlay.Label ?? string.Empty;
            roi.StrokeColor = color;
            roi.StrokeThickness = overlay.Type == VisionOverlayType.Region ? 1 : Math.Max(1, overlay.StrokeWidth);
            roi.IsLocked = true;
            roi.IsVisible = _currentImage is not null && ShowOverlaysCheckBox.IsChecked == true;
        }
        return rois;
    }

    private void UpdateExternalOverlayVisibility()
    {
        if (_externalViewer is null)
        {
            return;
        }
        var wasDirty = _externalViewer.IsDirty;
        foreach (var roi in _externalOverlayRois)
        {
            roi.IsVisible = _currentImage is not null && ShowOverlaysCheckBox.IsChecked == true;
        }
        if (!wasDirty)
        {
            _externalViewer.MarkDocumentClean();
        }
    }

    private static VisionRoi? ConvertExternalRoiToVisionRoi(global::ImageViewer.Models.RoiBase roi) => roi switch
    {
        global::ImageViewer.Models.BlobAnalysisRoi blob => new VisionRoi(
            VisionRoiType.Rectangle,
            X: Math.Round(blob.Center.X - blob.Width / 2.0, 2),
            Y: Math.Round(blob.Center.Y - blob.Height / 2.0, 2),
            Width: Math.Round(blob.Width, 2),
            Height: Math.Round(blob.Height, 2)),
        global::ImageViewer.Models.RotatedRect rect when Math.Abs(rect.Angle) < 0.01 => new VisionRoi(
            VisionRoiType.Rectangle,
            X: Math.Round(rect.Center.X - rect.Width / 2.0, 2),
            Y: Math.Round(rect.Center.Y - rect.Height / 2.0, 2),
            Width: Math.Round(rect.Width, 2),
            Height: Math.Round(rect.Height, 2)),
        global::ImageViewer.Models.RotatedRect rotated => BuildRotatedRectPolygon(rotated),
        global::ImageViewer.Models.CircleRoi circle => new VisionRoi(
            VisionRoiType.Circle,
            X: Math.Round(circle.Center.X, 2),
            Y: Math.Round(circle.Center.Y, 2),
            Radius: Math.Round(circle.Radius, 2)),
        global::ImageViewer.Models.PolygonRoi polygon when polygon.Points.Count >= 3 => new VisionRoi(
            VisionRoiType.Polygon,
            Points: polygon.Points.Select(p => new VisionRoiPoint(Math.Round(p.X, 2), Math.Round(p.Y, 2))).ToArray()),
        _ => null
    };

    private static VisionRoi BuildRotatedRectPolygon(global::ImageViewer.Models.RotatedRect rect)
    {
        var halfW = rect.Width / 2.0;
        var halfH = rect.Height / 2.0;
        var rad = rect.Angle * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var local = new[]
        {
            new Point(-halfW, -halfH),
            new Point(halfW, -halfH),
            new Point(halfW, halfH),
            new Point(-halfW, halfH)
        };
        var points = local.Select(p => new VisionRoiPoint(
            Math.Round(rect.Center.X + p.X * cos - p.Y * sin, 2),
            Math.Round(rect.Center.Y + p.X * sin + p.Y * cos, 2))).ToArray();
        return new VisionRoi(VisionRoiType.Polygon, Points: points);
    }

    private static global::ImageViewer.Models.RoiBase? ConvertVisionRoiToExternalRoi(VisionRoi roi) => roi.Type switch
    {
        VisionRoiType.Rectangle when roi.X is { } x && roi.Y is { } y && roi.Width is { } w && roi.Height is { } h =>
            new global::ImageViewer.Models.RotatedRect
            {
                Center = new(x + w / 2.0, y + h / 2.0),
                Width = Math.Max(1, w),
                Height = Math.Max(1, h),
                Angle = 0,
                Label = "ROI"
            },
        VisionRoiType.Circle when roi.X is { } cx && roi.Y is { } cy && roi.Radius is { } r =>
            new global::ImageViewer.Models.CircleRoi
            {
                Center = new(cx, cy),
                Radius = Math.Max(1, r),
                Label = "ROI"
            },
        VisionRoiType.Polygon when roi.Points is { Count: >= 3 } pts =>
            new global::ImageViewer.Models.PolygonRoi
            {
                IsClosed = true,
                Points = new ObservableCollection<global::ImageViewer.Models.PointD>(pts.Select(p => new global::ImageViewer.Models.PointD(p.X, p.Y))),
                Label = "ROI"
            },
        _ => null
    };
#endif

    public VisionRoi? TryGetActiveUserRoi()
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            var selected = _externalViewer.ViewerState.SelectedRoi;
            if (selected is not null && !selected.IsLocked && !_externalOverlayRois.Contains(selected))
            {
                if (ConvertExternalRoiToVisionRoi(selected) is { } selectedVisionRoi)
                    return selectedVisionRoi;
            }

            for (var i = _externalViewer.ViewerState.AllRois.Count - 1; i >= 0; i--)
            {
                var candidate = _externalViewer.ViewerState.AllRois[i];
                if (candidate.IsLocked || _externalOverlayRois.Contains(candidate))
                    continue;
                if (ConvertExternalRoiToVisionRoi(candidate) is { } converted)
                    return converted;
            }
        }
#endif
        return null;
    }

    public void ShowUserRoi(VisionRoi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null && ConvertVisionRoiToExternalRoi(roi) is { } externalRoi)
        {
            var userRois = _externalViewer.ViewerState.AllRois.Except(_externalOverlayRois).ToList();
            userRois.Add(externalRoi);
            _externalViewer.SetRois(userRois.Concat(_externalOverlayRois));
            _externalViewer.ViewerState.SelectedRoi = externalRoi;
        }
#endif
    }

    public void ClearUserRois()
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            _externalViewer.SetRois(_externalOverlayRois);
            _externalViewer.ViewerState.SelectedRoi = null;
        }
#endif
    }

    private void OnDrawRectRoiClick(object sender, RoutedEventArgs e)
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null && _currentImage is not null)
            _externalViewer.StartRoiMode();
#endif
    }

    private void OnDrawCircleRoiClick(object sender, RoutedEventArgs e)
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null && _currentImage is not null)
            _externalViewer.StartCircleRoiMode();
#endif
    }

    private void OnDrawPolygonRoiClick(object sender, RoutedEventArgs e)
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null && _currentImage is not null)
            _externalViewer.StartPolygonRoiMode();
#endif
    }

    private void OnClearUserRoisClick(object sender, RoutedEventArgs e) => ClearUserRois();

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToView();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => ResetActualSize();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ApplyZoomFactor(1.25, new Point(ViewportHostGrid.ActualWidth / 2, ViewportHostGrid.ActualHeight / 2));

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ApplyZoomFactor(0.8, new Point(ViewportHostGrid.ActualWidth / 2, ViewportHostGrid.ActualHeight / 2));

    private void OnOverlayVisibilityChanged(object sender, RoutedEventArgs e)
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            UpdateExternalOverlayVisibility();
            return;
        }
#endif
        RenderBuiltinOverlays();
    }

    private void OnOpenLocalImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图像文件",
            Filter = "图像文件 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(dialog.FileName, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        SetImage(bitmap, System.IO.Path.GetFileName(dialog.FileName), fitToView: true);
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsExternalImageViewerActive || _currentImage is null)
        {
            return;
        }

        var factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        ApplyZoomFactor(factor, e.GetPosition(ViewportHostGrid));
        e.Handled = true;
    }

    private void ApplyZoomFactor(double factor, Point pivotInViewport)
    {
#if USE_EXTERNAL_IMAGE_VIEWER
        if (_externalViewer is not null)
        {
            _externalViewer.ZoomAtViewportCenter(factor);
            return;
        }
#endif
        var oldScale = ZoomTransform.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, 0.05, 32.0);
        if (Math.Abs(newScale - oldScale) < 1e-6)
        {
            return;
        }

        var imageX = (pivotInViewport.X - PanTransform.X) / oldScale;
        var imageY = (pivotInViewport.Y - PanTransform.Y) / oldScale;

        ZoomTransform.ScaleX = newScale;
        ZoomTransform.ScaleY = newScale;
        PanTransform.X = pivotInViewport.X - imageX * newScale;
        PanTransform.Y = pivotInViewport.Y - imageY * newScale;
        UpdateZoomText();
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsExternalImageViewerActive || _currentImage is null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            FitToView();
            return;
        }

        _isDragging = true;
        _lastDragPoint = e.GetPosition(ViewportHostGrid);
        ViewportHostGrid.CaptureMouse();
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (IsExternalImageViewerActive || _currentImage is null)
        {
            return;
        }

        var pos = e.GetPosition(ViewportHostGrid);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            PanTransform.X += pos.X - _lastDragPoint.X;
            PanTransform.Y += pos.Y - _lastDragPoint.Y;
            _lastDragPoint = pos;
        }

        var imgX = (int)Math.Floor((pos.X - PanTransform.X) / ZoomTransform.ScaleX);
        var imgY = (int)Math.Floor((pos.Y - PanTransform.Y) / ZoomTransform.ScaleY);
        PixelInfoText.Text = VisionImageInterop.ReadPixelText(_currentImage, imgX, imgY);
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ViewportHostGrid.ReleaseMouseCapture();
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0}%", ZoomTransform.ScaleX * 100);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
#if USE_EXTERNAL_IMAGE_VIEWER
        BindingOperations.ClearBinding(ZoomText, TextBlock.TextProperty);
        _externalOverlayRois.Clear();
        _externalViewer?.Dispose();
        _externalHost?.Dispose();
#endif
        GC.SuppressFinalize(this);
    }
}
