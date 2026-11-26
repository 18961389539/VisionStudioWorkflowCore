using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Dialogs;

/// <summary>Shows a before/after review of suggested edge-detection parameters.</summary>
internal sealed class DetectionParameterRecommendationDialog : Window
{
    private const int CropPadding = 12;
    private const int MaximumFallbackCropDimension = 1024;
    private const int MaximumPreviewCropPixels = 4 * 1024 * 1024;
    private readonly DetectionRecommendationPreview _preview;

    internal DataGrid ParameterGrid { get; }
    internal Button ApplyButton { get; }
    internal Image BeforeImage { get; }
    internal Image AfterImage { get; }
    internal Canvas BeforeOverlay { get; }
    internal Canvas AfterOverlay { get; }
    internal Int32Rect PreviewCropRect { get; }
    internal RoiBase? AcceptedRoi { get; private set; }

    /// <summary>Creates a dialog over an immutable preview and image snapshot.</summary>
    internal DetectionParameterRecommendationDialog(DetectionRecommendationPreview preview, BitmapSource bitmap)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        ArgumentNullException.ThrowIfNull(bitmap);

        Title = UiText.Get("DetectionRecommendationTitle");
        Width = 1120;
        Height = 780;
        MinWidth = 780;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        AutomationProperties.SetName(this, Title);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("DetectionRecommendationTitle"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("DetectionRecommendationHeuristicDisclaimer"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var body = new StackPanel { Orientation = Orientation.Vertical };
        content.Content = body;
        if (!preview.CanApply)
        {
            string blockedKey = string.IsNullOrWhiteSpace(preview.ApplyBlockedKey)
                ? "DetectionRecommendationTrialUnavailable"
                : preview.ApplyBlockedKey;
            body.Children.Add(new TextBlock
            {
                Text = UiText.Get(blockedKey),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.ControlTextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        body.Children.Add(BuildEvidencePanel(preview.Proposal));

        ParameterGrid = BuildParameterGrid(preview.Proposal.Changes);
        body.Children.Add(ParameterGrid);

        PreviewCropRect = GetSharedCropRect(preview.Before, preview.After, bitmap.PixelWidth, bitmap.PixelHeight);
        var trialGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        trialGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trialGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (BeforeImage, BeforeOverlay) = BuildTrialPanel(trialGrid, 0, UiText.Get("DetectionRecommendationBeforeTrial"), preview.Before, bitmap, PreviewCropRect);
        (AfterImage, AfterOverlay) = BuildTrialPanel(trialGrid, 1, UiText.Get("DetectionRecommendationAfterTrial"), preview.After, bitmap, PreviewCropRect);
        body.Children.Add(trialGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        ApplyButton = new Button
        {
            Content = UiText.Get("DetectionRecommendationApply"),
            IsEnabled = preview.CanApply && preview.After.DetectedRoi is not null,
            MinWidth = 112,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        AutomationProperties.SetName(ApplyButton, UiText.Get("DetectionRecommendationApply"));
        ApplyButton.Click += (_, _) => ApplyRecommendation();
        var cancelButton = new Button
        {
            Content = UiText.Get("DetectionRecommendationCancel"),
            MinWidth = 96,
            IsCancel = true
        };
        AutomationProperties.SetName(cancelButton, UiText.Get("DetectionRecommendationCancel"));
        cancelButton.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ApplyButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
    }

    private void ApplyRecommendation()
    {
        if (!ApplyButton.IsEnabled || _preview.After.DetectedRoi is null)
        {
            return;
        }

        AcceptedRoi = _preview.After.DetectedRoi.Clone();
        DialogResult = true;
    }

    private static Border BuildEvidencePanel(DetectionParameterProposal proposal)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("DetectionRecommendationEvidence"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });

        DetectionParameterEvidence evidence = proposal.Evidence;
        var metrics = new WrapPanel();
        metrics.Children.Add(CreateEvidenceItem("DetectionRecommendationContrast", FormatNumber(evidence.Contrast)));
        metrics.Children.Add(CreateEvidenceItem("DetectionRecommendationNoise", FormatNumber(evidence.Noise)));
        metrics.Children.Add(CreateEvidenceItem("DetectionRecommendationPolarity", FormatPercent(evidence.DominantPolarityRatio)));
        metrics.Children.Add(CreateEvidenceItem("DetectionRecommendationSampleBudget", evidence.SampleCount.ToString(CultureInfo.CurrentCulture)));
        panel.Children.Add(metrics);

        if (evidence.Notes.Length > 0)
        {
            foreach (string noteKey in evidence.Notes)
            {
                panel.Children.Add(new TextBlock { Text = UiText.Get(noteKey), TextWrapping = TextWrapping.Wrap });
            }
        }

        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("DetectionRecommendationMethod"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        if (!proposal.Success && !string.IsNullOrWhiteSpace(proposal.FailureKey))
        {
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get(proposal.FailureKey),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.ControlTextBrush,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ActiveBorderBrush,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private static FrameworkElement CreateEvidenceItem(string key, string value)
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 20, 4) };
        item.Children.Add(new TextBlock { Text = UiText.Get(key), Foreground = SystemColors.GrayTextBrush });
        item.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold });
        return item;
    }

    private static DataGrid BuildParameterGrid(IReadOnlyList<DetectionParameterChange> changes)
    {
        var grid = new DataGrid
        {
            ItemsSource = changes.Select(change => new ComparisonRow(
                UiText.Get(change.NameKey),
                FormatComparisonValue(change.PropertyName, change.Before),
                FormatComparisonValue(change.PropertyName, change.After),
                UiText.Get(change.ReasonKey))).ToArray(),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            MinHeight = changes.Count == 0 ? 58 : Math.Min(230, changes.Count * 34 + 34),
            MaxHeight = 230
        };
        AutomationProperties.SetName(grid, UiText.Get("DetectionRecommendationParameters"));
        grid.Columns.Add(CreateColumn("Name", "DetectionRecommendationParameter", 1.0));
        grid.Columns.Add(CreateColumn("Before", "DetectionRecommendationBefore", 0.8));
        grid.Columns.Add(CreateColumn("After", "DetectionRecommendationAfter", 0.8));
        grid.Columns.Add(CreateColumn("Reason", "DetectionRecommendationReason", 1.5));
        return grid;
    }

    private static string FormatComparisonValue(string propertyName, string value)
    {
        if (bool.TryParse(value, out bool boolean))
        {
            return UiText.Get(boolean ? "RecommendationValueTrue" : "RecommendationValueFalse");
        }

        if (string.Equals(propertyName, "CaliperEdgePolarity", StringComparison.Ordinal) &&
            Enum.TryParse(value, ignoreCase: false, out CaliperEdgePolarity polarity))
        {
            return polarity switch
            {
                CaliperEdgePolarity.DarkToLight => UiText.Get("RecommendationValueDarkToLight"),
                CaliperEdgePolarity.LightToDark => UiText.Get("RecommendationValueLightToDark"),
                _ => UiText.Get("RecommendationValueAny")
            };
        }

        return value;
    }

    private static DataGridTextColumn CreateColumn(string bindingPath, string headerKey, double width)
    {
        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        cellStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
        cellStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(bindingPath)));
        return new DataGridTextColumn
        {
            Header = UiText.Get(headerKey),
            Binding = new Binding(bindingPath),
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            ElementStyle = cellStyle,
            IsReadOnly = true
        };
    }

    private static (Image Image, Canvas Overlay) BuildTrialPanel(
        Grid grid,
        int column,
        string title,
        DetectionTrial trial,
        BitmapSource source,
        Int32Rect crop)
    {
        BitmapSource display = CreateDisplayCrop(source, crop);

        var panel = new Border
        {
            Padding = new Thickness(9),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ActiveBorderBrush,
            Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, 0)
        };
        var stack = new StackPanel();
        panel.Child = stack;
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });

        var summary = new TextBlock
        {
            Text = FormatTrialSummary(trial),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetName(summary, title);
        stack.Children.Add(summary);

        var viewbox = new Viewbox { Stretch = Stretch.Uniform, MaxHeight = 250, MinHeight = 120 };
        var imageAndOverlay = new Grid { Width = display.PixelWidth, Height = display.PixelHeight, ClipToBounds = true };
        RenderOptions.SetBitmapScalingMode(imageAndOverlay, BitmapScalingMode.NearestNeighbor);
        var image = new Image { Source = display, Width = display.PixelWidth, Height = display.PixelHeight, Stretch = Stretch.Fill };
        var overlay = new Canvas { Width = display.PixelWidth, Height = display.PixelHeight };
        imageAndOverlay.Children.Add(image);
        imageAndOverlay.Children.Add(overlay);
        AddPointMarkers(overlay, trial.Points, crop, display.PixelWidth, display.PixelHeight);
        viewbox.Child = imageAndOverlay;
        stack.Children.Add(viewbox);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        return (image, overlay);
    }

    private static string FormatTrialSummary(DetectionTrial trial)
    {
        string statusKey = trial.StatusKey;
        if (string.IsNullOrWhiteSpace(statusKey))
        {
            statusKey = trial.Success ? "DetectionRecommendationSuccessfulBaseline" : "DetectionRecommendationTrialUnavailable";
        }

        string score = trial.Score is double scoreValue ? FormatNumber(scoreValue) : "—";
        string residual = trial.Residual is double residualValue ? FormatNumber(residualValue) : "—";
        string validSamples = trial.DetectedRoi is BlobAnalysisRoi blob
            ? UiText.Format("DetectionRecommendationBlobTrialMetrics", blob.DetectedBlobs.Count,
                blob.DetectedBlobs.Count == 0 ? 0 : blob.DetectedBlobs.Max(item => item.Area), trial.Points.Length)
            : UiText.Format("DetectionRecommendationTrialMetrics", score, residual, trial.ValidSamples, trial.TotalSamples, trial.Points.Length);
        return UiText.Get(statusKey) + " " + validSamples;
    }

    private static void AddPointMarkers(Canvas canvas, IReadOnlyList<PointD> points, Int32Rect crop, int displayWidth, int displayHeight)
    {
        foreach (PointD point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < crop.X || point.X >= crop.X + crop.Width || point.Y < crop.Y || point.Y >= crop.Y + crop.Height)
            {
                continue;
            }

            var marker = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Brushes.OrangeRed,
                Stroke = Brushes.White,
                StrokeThickness = 0.8,
                ToolTip = UiText.Format("DetectionRecommendationPointTooltip", FormatNumber(point.X), FormatNumber(point.Y))
            };
            Canvas.SetLeft(marker, (point.X - crop.X) * displayWidth / crop.Width - marker.Width / 2);
            Canvas.SetTop(marker, (point.Y - crop.Y) * displayHeight / crop.Height - marker.Height / 2);
            canvas.Children.Add(marker);
        }
    }

    private static Int32Rect GetSharedCropRect(DetectionTrial before, DetectionTrial after, int imageWidth, int imageHeight)
    {
        var bounds = new List<PointD>(before.Points.Length + after.Points.Length + 16);
        AddTrialBounds(bounds, before);
        AddTrialBounds(bounds, after);
        bounds.RemoveAll(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y));

        if (bounds.Count == 0)
        {
            return CreateFallbackCrop(imageWidth, imageHeight);
        }

        double minX = bounds.Min(point => point.X);
        double minY = bounds.Min(point => point.Y);
        double maxX = bounds.Max(point => point.X);
        double maxY = bounds.Max(point => point.Y);
        if (maxX < -CropPadding || maxY < -CropPadding || minX > imageWidth + CropPadding || minY > imageHeight + CropPadding)
        {
            return CreateFallbackCrop(imageWidth, imageHeight);
        }

        // Clamp in floating point first so even extreme, finite off-image geometry cannot overflow int casts.
        int x = (int)Math.Floor(ClampToImage(minX - CropPadding, imageWidth - 1));
        int y = (int)Math.Floor(ClampToImage(minY - CropPadding, imageHeight - 1));
        int right = (int)Math.Ceiling(ClampToImage(maxX + CropPadding + 1, imageWidth));
        int bottom = (int)Math.Ceiling(ClampToImage(maxY + CropPadding + 1, imageHeight));
        right = Math.Clamp(right, x + 1, imageWidth);
        bottom = Math.Clamp(bottom, y + 1, imageHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static void AddTrialBounds(List<PointD> bounds, DetectionTrial trial)
    {
        bounds.AddRange(trial.Points);
        AddRoiBounds(bounds, trial.DetectedRoi);
    }

    private static void AddRoiBounds(List<PointD> bounds, RoiBase? roi)
    {
        switch (roi)
        {
            case CaliperMeasureRoi dual:
                bounds.AddRange(GetDualCaliperSearchCorners(dual));
                break;
            case LineCaliperMeasureRoi lineCaliper:
                bounds.AddRange(GetLineCaliperSearchCorners(lineCaliper));
                break;
            case CircularCaliperMeasureRoi circularCaliper:
                double radialExtent = Math.Abs(circularCaliper.Radius) + Math.Max(0, circularCaliper.CaliperSearchRange);
                bounds.Add(new PointD(circularCaliper.Center.X - radialExtent, circularCaliper.Center.Y - radialExtent));
                bounds.Add(new PointD(circularCaliper.Center.X + radialExtent, circularCaliper.Center.Y + radialExtent));
                break;
            case RotatedRect rectangle:
                bounds.AddRange(GetRotatedCorners(rectangle));
                break;
            case CircleRoi circle:
                bounds.Add(new PointD(circle.Center.X - Math.Abs(circle.Radius), circle.Center.Y - Math.Abs(circle.Radius)));
                bounds.Add(new PointD(circle.Center.X + Math.Abs(circle.Radius), circle.Center.Y + Math.Abs(circle.Radius)));
                break;
            case PolygonRoi polygon:
                bounds.AddRange(polygon.Points);
                break;
            case PolylineRoi polyline:
                bounds.AddRange(polyline.Points);
                break;
            case LineMeasureRoi line:
                bounds.Add(line.P1);
                bounds.Add(line.P2);
                break;
        }
    }

    private static IEnumerable<PointD> GetDualCaliperSearchCorners(CaliperMeasureRoi caliper)
    {
        CaliperMeasureRoi region = caliper;
        if (!caliper.HasExplicitCaliperRegion)
        {
            region = (CaliperMeasureRoi)caliper.Clone();
            region.EnsureCaliperRegion();
        }

        double angle = region.CaliperAngleDegrees * Math.PI / 180.0;
        var measurement = new VectorD(Math.Cos(angle), Math.Sin(angle));
        var tangent = new VectorD(-measurement.Y, measurement.X);
        double search = Math.Max(0, region.CaliperSearchRange);
        double halfRegion = Math.Max(0, region.GetResolvedCaliperRegionLength()) / 2;
        foreach (double along in new[] { -search, search })
        {
            foreach (double across in new[] { -halfRegion, halfRegion })
            {
                yield return new PointD(
                    region.CaliperCenter.X + measurement.X * along + tangent.X * across,
                    region.CaliperCenter.Y + measurement.Y * along + tangent.Y * across);
            }
        }
    }

    private static IEnumerable<PointD> GetLineCaliperSearchCorners(LineCaliperMeasureRoi lineCaliper)
    {
        double dx = lineCaliper.P2.X - lineCaliper.P1.X;
        double dy = lineCaliper.P2.Y - lineCaliper.P1.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 0)
        {
            yield return lineCaliper.P1;
            yield return lineCaliper.P2;
            yield break;
        }

        double nx = -dy / length;
        double ny = dx / length;
        double search = Math.Max(0, lineCaliper.CaliperSearchRange);
        foreach (PointD endpoint in new[] { lineCaliper.P1, lineCaliper.P2 })
        {
            yield return new PointD(endpoint.X + nx * search, endpoint.Y + ny * search);
            yield return new PointD(endpoint.X - nx * search, endpoint.Y - ny * search);
        }
    }

    private static double ClampToImage(double value, int maximum)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }

        return value >= maximum ? maximum : value;
    }

    private static Int32Rect CreateFallbackCrop(int imageWidth, int imageHeight)
    {
        int width = Math.Min(imageWidth, MaximumFallbackCropDimension);
        int height = Math.Min(imageHeight, MaximumFallbackCropDimension);
        return new Int32Rect((imageWidth - width) / 2, (imageHeight - height) / 2, width, height);
    }

    private static IEnumerable<PointD> GetRotatedCorners(RotatedRect rectangle)
    {
        double radians = rectangle.Angle * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double halfWidth = Math.Abs(rectangle.Width) / 2;
        double halfHeight = Math.Abs(rectangle.Height) / 2;
        foreach ((double x, double y) in new[] { (-halfWidth, -halfHeight), (halfWidth, -halfHeight), (halfWidth, halfHeight), (-halfWidth, halfHeight) })
        {
            yield return new PointD(rectangle.Center.X + x * cos - y * sin, rectangle.Center.Y + x * sin + y * cos);
        }
    }

    private static BitmapSource CreateDisplayCrop(BitmapSource source, Int32Rect crop)
    {
        var cropped = new CroppedBitmap(source, crop);
        BitmapSource bounded = DownsampleForPreview(cropped);
        if (source.Format == PixelFormats.Gray16)
        {
            return CreateGray16ContrastCrop(bounded);
        }

        BitmapSource display = bounded.Format == PixelFormats.Bgra32
            ? bounded
            : new FormatConvertedBitmap(bounded, PixelFormats.Bgra32, null, 0);
        if (display.CanFreeze && !display.IsFrozen)
        {
            display.Freeze();
        }

        return display;
    }

    private static BitmapSource DownsampleForPreview(BitmapSource source)
    {
        long area = (long)source.PixelWidth * source.PixelHeight;
        if (area <= MaximumPreviewCropPixels)
        {
            return source;
        }

        double scale = Math.Sqrt(MaximumPreviewCropPixels / (double)area);
        int width = Math.Max(1, (int)Math.Floor(source.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Floor(source.PixelHeight * scale));
        if (height == 1)
        {
            width = Math.Min(width, MaximumPreviewCropPixels);
        }
        if (width == 1)
        {
            height = Math.Min(height, MaximumPreviewCropPixels);
        }
        while ((long)width * height > MaximumPreviewCropPixels)
        {
            if (width >= height)
            {
                width--;
            }
            else
            {
                height--;
            }
        }

        var transformed = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        RenderOptions.SetBitmapScalingMode(transformed, BitmapScalingMode.NearestNeighbor);
        if (transformed.CanFreeze && !transformed.IsFrozen)
        {
            transformed.Freeze();
        }

        return transformed;
    }

    private static BitmapSource CreateGray16ContrastCrop(BitmapSource cropped)
    {
        int width = cropped.PixelWidth;
        int height = cropped.PixelHeight;
        int stride = checked(width * sizeof(ushort));
        byte[] sourcePixels = new byte[checked(stride * height)];
        cropped.CopyPixels(sourcePixels, stride, 0);
        ushort minimum = ushort.MaxValue;
        ushort maximum = ushort.MinValue;
        for (int offset = 0; offset < sourcePixels.Length; offset += sizeof(ushort))
        {
            ushort value = BitConverter.ToUInt16(sourcePixels, offset);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        int outputStride = checked(width * 4);
        byte[] output = new byte[checked(outputStride * height)];
        double range = maximum - minimum;
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            ushort value = BitConverter.ToUInt16(sourcePixels, pixel * sizeof(ushort));
            byte gray = range <= 0 ? (byte)128 : (byte)Math.Clamp((int)Math.Round((value - minimum) * 255.0 / range), 0, 255);
            int offset = pixel * 4;
            output[offset] = gray;
            output[offset + 1] = gray;
            output[offset + 2] = gray;
            output[offset + 3] = byte.MaxValue;
        }

        var bitmap = BitmapSource.Create(width, height, cropped.DpiX, cropped.DpiY, PixelFormats.Bgra32, null, output, outputStride);
        bitmap.Freeze();
        return bitmap;
    }

    private static string FormatNumber(double value) => double.IsFinite(value) ? value.ToString("0.###", CultureInfo.CurrentCulture) : "—";
    private static string FormatPercent(double value) => double.IsFinite(value) ? (value * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%" : "—";

    private sealed record ComparisonRow(string Name, string Before, string After, string Reason);
}
