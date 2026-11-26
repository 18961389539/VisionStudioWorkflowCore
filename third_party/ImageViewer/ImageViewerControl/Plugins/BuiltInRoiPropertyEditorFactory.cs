using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Plugins
{
    internal static class BuiltInRoiPropertyEditorFactory
    {
        public static FrameworkElement? CreateEditor(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            return roi switch
            {
                BlobAnalysisRoi typed => CreateEditor(typed),
                RotatedRect typed => CreateEditor(typed),
                ArcCaliperMeasureRoi typed => CreateEditor(typed),
                CaliperMeasureRoi typed => CreateEditor(typed),
                LineCaliperMeasureRoi typed => CreateEditor(typed),
                CircularCaliperMeasureRoi typed => CreateEditor(typed),
                EllipseRoi typed => CreateEditor(typed),
                CircleRoi typed => CreateEditor(typed),
                RingRoi typed => CreateEditor(typed),
                PolygonRoi typed => CreateEditor(typed),
                PolylineRoi typed => CreateEditor(typed),
                PointAnnotationRoi typed => CreateEditor(typed),
                PointCoordinateMeasureRoi typed => CreateEditor(typed),
                TextAnnotationRoi typed => CreateEditor(typed),
                LineMeasureRoi typed => CreateEditor(typed),
                AngleMeasureRoi typed => CreateEditor(typed),
                ArcMeasureRoi typed => CreateEditor(typed),
                PointToLineDistanceRoi typed => CreateEditor(typed),
                PointToCircleDistanceRoi typed => CreateEditor(typed),
                ParallelismMeasureRoi typed => CreateEditor(typed),
                PerpendicularityMeasureRoi typed => CreateEditor(typed),
                ConcentricityMeasureRoi typed => CreateEditor(typed),
                CenterDistanceMeasureRoi typed => CreateEditor(typed),
                ThreePointCircleMeasureRoi typed => CreateEditor(typed),
                _ => null
            };
        }

        public static FrameworkElement CreateEditor(RotatedRect roi) => CreatePanel(roi, nameof(RotatedRect.Width), nameof(RotatedRect.Height), nameof(RotatedRect.Angle), nameof(RotatedRect.Center));

        public static FrameworkElement CreateEditor(BlobAnalysisRoi roi)
        {
            StackPanel panel = CreatePanel(roi, nameof(RotatedRect.Width), nameof(RotatedRect.Height), nameof(RotatedRect.Angle), nameof(RotatedRect.Center));
            AddCheckBox(panel, UiText.Get("EditorUseOtsu"), nameof(BlobAnalysisRoi.UseOtsu));
            AddSlider(panel, UiText.Get("EditorManualThreshold"), nameof(BlobAnalysisRoi.ManualThreshold), 0, 255, true);
            AddCheckBox(panel, UiText.Get("EditorDetectDark"), nameof(BlobAnalysisRoi.DetectDark));
            AddSlider(panel, UiText.Get("EditorMinArea"), nameof(BlobAnalysisRoi.MinArea), 1, 10000, true);
            return panel;
        }

        public static FrameworkElement CreateEditor(EllipseRoi roi) => CreatePanel(roi, nameof(EllipseRoi.RadiusX), nameof(EllipseRoi.RadiusY), nameof(EllipseRoi.Angle), nameof(EllipseRoi.Center));

        public static FrameworkElement CreateEditor(CircleRoi roi) => CreatePanel(roi, nameof(CircleRoi.Radius), readOnlyPointPath: nameof(CircleRoi.Center));

        public static FrameworkElement CreateEditor(RingRoi roi)
        {
            var panel = CreateBasePanel(roi);
            AddSlider(panel, UiText.Get("EditorInnerRadius"), nameof(RingRoi.InnerRadius), 0, 500);
            AddSlider(panel, UiText.Get("EditorOuterRadius"), nameof(RingRoi.OuterRadius), 0, 500);
            AddReadOnlyText(panel, UiText.Get("EditorCenter"), nameof(RingRoi.Center));
            return panel;
        }

        public static FrameworkElement CreateEditor(ArcCaliperMeasureRoi roi)
        {
            var panel = CreateBasePanel(roi);
            AddSlider(panel, UiText.Get("EditorRadius"), nameof(ArcCaliperMeasureRoi.Radius), 1, 500);
            AddSlider(panel, UiText.Get("EditorStartAngle"), nameof(ArcCaliperMeasureRoi.StartAngle), -360, 360);
            AddSlider(panel, UiText.Get("EditorSweepAngle"), nameof(ArcCaliperMeasureRoi.SweepAngle), -360, 360);
            AddSlider(panel, UiText.Get("EditorCaliperCount"), nameof(ArcCaliperMeasureRoi.CaliperCount), 6, 128, true);
            AddSlider(panel, UiText.Get("EditorSearchRange"), nameof(ArcCaliperMeasureRoi.CaliperSearchRange), 1, 100, true);
            AddSlider(panel, UiText.Get("EditorSamplingHalfWidth"), nameof(ArcCaliperMeasureRoi.CaliperSamplingHalfWidth), 0, 20, true);
            AddSlider(panel, UiText.Get("EditorMinimumValidCalipers"), nameof(ArcCaliperMeasureRoi.MinimumValidCalipers), 3, 128, true);
            AddSlider(panel, UiText.Get("EditorMinimumGradient"), nameof(ArcCaliperMeasureRoi.CaliperMinimumGradient), 0, 255);
            AddSlider(panel, UiText.Get("EditorOutlierThreshold"), nameof(ArcCaliperMeasureRoi.CaliperOutlierThreshold), 0, 20);
            AddEnumComboBox<CaliperEdgePolarity>(panel, UiText.Get("EditorEdgePolarity"), nameof(ArcCaliperMeasureRoi.CaliperEdgePolarity));
            AddReadOnlyText(panel, UiText.Get("EditorCenter"), nameof(ArcCaliperMeasureRoi.Center));
            return panel;
        }

        public static FrameworkElement CreateEditor(PolygonRoi roi)
        {
            Point[] points = roi.Points.ToWpfPointArray();
            var metrics = GeometryUtils.GetPolygonMetrics(points);
            string details = roi.IsClosed && points.Length >= 3
                ? UiText.FormatInvariant("EditorPolygonMeasureDetails", points.Length, metrics.Area, metrics.Perimeter, metrics.Centroid.X, metrics.Centroid.Y)
                : UiText.FormatInvariant("EditorPolygonOpenDetails", points.Length, GeometryUtils.PolylineLength(points));
            return CreatePanel(roi, readOnlyText: details);
        }

        public static FrameworkElement CreateEditor(PolylineRoi roi)
        {
            Point[] points = roi.Points.ToWpfPointArray();
            IReadOnlyList<double> segments = GeometryUtils.GetPolylineSegmentLengths(points);
            string details = UiText.FormatInvariant("EditorPolylineMeasureDetails", points.Length, segments.Count, GeometryUtils.PolylineLength(points), roi.IsFreehand ? UiText.Get("CommonYes") : UiText.Get("CommonNo"));
            return CreatePanel(roi, readOnlyText: details);
        }

        public static FrameworkElement CreateEditor(PointAnnotationRoi roi) => CreatePanel(roi, readOnlyPointPath: nameof(PointAnnotationRoi.Position));

        public static FrameworkElement CreateEditor(PointCoordinateMeasureRoi roi)
        {
            string details = UiText.FormatInvariant("EditorPointCoordinateDetails", roi.Position.X, roi.Position.Y, roi.IsEdgeSnapped ? UiText.Get("CommonYes") : UiText.Get("CommonNo"), roi.EdgeConfidence * 100);
            return CreatePanel(roi, readOnlyPointPath: nameof(PointCoordinateMeasureRoi.Position), readOnlyText: details);
        }

        public static FrameworkElement CreateEditor(TextAnnotationRoi roi) => CreatePanel(roi, readOnlyPointPath: nameof(TextAnnotationRoi.Position), includeLabel: true);

        public static FrameworkElement CreateEditor(LineMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorLineMeasureDetails", roi.P1.X, roi.P1.Y, roi.P2.X, roi.P2.Y));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(CaliperMeasureRoi roi)
        {
            var panel = CreateBasePanel(roi);
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(LineCaliperMeasureRoi roi)
        {
            var panel = CreateBasePanel(roi);
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(CircularCaliperMeasureRoi roi)
        {
            var panel = CreateBasePanel(roi);
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(AngleMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorAngleDetails", roi.P1.X, roi.P1.Y, roi.Vertex.X, roi.Vertex.Y, roi.P2.X, roi.P2.Y));
            // 主测量值 = 三点夹角（度），判定即比较该角度值。
            AppendMeasuredQuantityNote(panel, UiText.Get("EditorMeasuredQuantityAngle"));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(ArcMeasureRoi roi)
        {
            string text = UiText.FormatInvariant("EditorArcDetailsBase", roi.StartPoint.X, roi.StartPoint.Y, roi.EndPoint.X, roi.EndPoint.Y, roi.ArcPoint.X, roi.ArcPoint.Y);
            if (roi.IsValid)
            {
                text += UiText.FormatInvariant("EditorArcDetailsRadius", roi.Radius);
                text += UiText.FormatInvariant("EditorArcDetailsLength", roi.ArcLength);
                text += UiText.FormatInvariant("EditorArcDetailsCentralAngle", roi.CentralAngle);
            }

            StackPanel panel = CreatePanel(roi, readOnlyText: text);
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(PointToLineDistanceRoi roi)
        {
            return CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorPointToLineDetails", roi.Point.X, roi.Point.Y, roi.LineP1.X, roi.LineP1.Y, roi.LineP2.X, roi.LineP2.Y, roi.Distance));
        }

        public static FrameworkElement CreateEditor(PointToCircleDistanceRoi roi)
        {
            return CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorPointToCircleDetails", roi.Point.X, roi.Point.Y, roi.Center.X, roi.Center.Y, roi.Radius, roi.DistanceToCircle, roi.DistanceToCenter));
        }

        public static FrameworkElement CreateEditor(ParallelismMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorParallelismDetails", roi.Line1P1.X, roi.Line1P1.Y, roi.Line1P2.X, roi.Line1P2.Y, roi.Line2P1.X, roi.Line2P1.Y, roi.Line2P2.X, roi.Line2P2.Y, roi.AngleDifference, roi.AverageDistance));
            AppendMeasuredQuantityNote(panel, UiText.Get("EditorMeasuredQuantityParallelism"));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(PerpendicularityMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorPerpendicularityDetails", roi.Line1P1.X, roi.Line1P1.Y, roi.Line1P2.X, roi.Line1P2.Y, roi.Line2P1.X, roi.Line2P1.Y, roi.Line2P2.X, roi.Line2P2.Y, roi.AngleBetweenLines, roi.PerpendicularityError));
            AppendMeasuredQuantityNote(panel, UiText.Get("EditorMeasuredQuantityPerpendicularity"));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(ConcentricityMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorConcentricityDetails", roi.Center1.X, roi.Center1.Y, roi.Radius1, roi.Center2.X, roi.Center2.Y, roi.Radius2, roi.CenterDistance));
            AppendMeasuredQuantityNote(panel, UiText.Get("EditorMeasuredQuantityConcentricity"));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(CenterDistanceMeasureRoi roi)
        {
            StackPanel panel = CreatePanel(roi, readOnlyText: UiText.FormatInvariant("EditorCenterDistanceDetails", roi.Center1.X, roi.Center1.Y, roi.Center2.X, roi.Center2.Y, roi.CenterDistance));
            AppendToleranceEditor(panel, roi);
            return panel;
        }

        public static FrameworkElement CreateEditor(ThreePointCircleMeasureRoi roi)
        {
            string text = UiText.FormatInvariant("EditorThreePointCircleDetails", roi.P1.X, roi.P1.Y, roi.P2.X, roi.P2.Y, roi.P3.X, roi.P3.Y);
            if (roi.IsValid)
            {
                text += UiText.FormatInvariant("EditorThreePointCircleResult", roi.Center.X, roi.Center.Y, roi.Radius);
            }

            return CreatePanel(roi, readOnlyText: text);
        }

        private static StackPanel CreateBasePanel(RoiBase roi, bool includeLabel = true)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0),
                DataContext = roi,
                MinWidth = 260
            };

            panel.Children.Add(CreateHeader(roi.DisplayTypeName));
            if (includeLabel)
            {
                AddTextEditor(panel, UiText.Get("EditorLabel"), nameof(RoiBase.Label));
            }

            AddSlider(panel, UiText.Get("EditorStrokeThickness"), nameof(RoiBase.StrokeThickness), 1, 12);
            AddCheckBox(panel, UiText.Get("EditorVisible"), nameof(RoiBase.IsVisible));
            AddCheckBox(panel, UiText.Get("EditorLocked"), nameof(RoiBase.IsLocked));
            return panel;
        }

        private static StackPanel CreatePanel(RoiBase roi, string? metricPath1 = null, string? metricPath2 = null, string? metricPath3 = null, string? readOnlyPointPath = null, string? readOnlyText = null, bool includeLabel = true)
        {
            StackPanel panel = CreateBasePanel(roi, includeLabel);

            if (metricPath1 != null)
            {
                AddSlider(panel, GetPropertyLabel(metricPath1), metricPath1, 0, 500);
            }

            if (metricPath2 != null)
            {
                AddSlider(panel, GetPropertyLabel(metricPath2), metricPath2, 0, 500);
            }

            if (metricPath3 != null)
            {
                AddSlider(panel, GetPropertyLabel(metricPath3), metricPath3, -180, 180);
            }

            if (readOnlyPointPath != null)
            {
                AddReadOnlyText(panel, GetPropertyLabel(readOnlyPointPath), readOnlyPointPath);
            }

            if (readOnlyText != null)
            {
                panel.Children.Add(CreateLabel(UiText.Get("EditorDetailsLabel")));
                panel.Children.Add(new TextBlock
                {
                    Text = readOnlyText,
                    Foreground = Brushes.WhiteSmoke,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return panel;
        }

        private static string GetPropertyLabel(string propertyPath)
        {
            return propertyPath switch
            {
                nameof(RotatedRect.Width) => UiText.Get("EditorWidth"),
                nameof(RotatedRect.Height) => UiText.Get("EditorHeight"),
                nameof(RotatedRect.Angle) => UiText.Get("EditorAngle"),
                nameof(RotatedRect.Center) => UiText.Get("EditorCenter"),
                nameof(EllipseRoi.RadiusX) => UiText.Get("EditorRadiusX"),
                nameof(EllipseRoi.RadiusY) => UiText.Get("EditorRadiusY"),
                nameof(CircleRoi.Radius) => UiText.Get("EditorRadius"),
                nameof(PointAnnotationRoi.Position) => UiText.Get("EditorPosition"),
                _ => propertyPath
            };
        }

        private static TextBlock CreateHeader(string text) => new()
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };

        private static TextBlock CreateLabel(string text) => new()
        {
            Text = text,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 6, 0, 2)
        };

        private static void AddTextEditor(StackPanel panel, string header, string propertyPath)
        {
            panel.Children.Add(CreateLabel(header));
            var textBox = new TextBox();
            textBox.SetBinding(TextBox.TextProperty, CreateTwoWayBinding(propertyPath));
            panel.Children.Add(textBox);
        }

        private static void AddSlider(StackPanel panel, string header, string propertyPath, double minimum, double maximum, bool isSnapToTickEnabled = false)
        {
            panel.Children.Add(CreateLabel(header));
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = 1,
                IsSnapToTickEnabled = isSnapToTickEnabled
            };
            slider.SetBinding(RangeBase.ValueProperty, CreateTwoWayBinding(propertyPath));
            panel.Children.Add(slider);
        }

        private static void AddEnumComboBox<TEnum>(StackPanel panel, string header, string propertyPath) where TEnum : struct, Enum
        {
            panel.Children.Add(CreateLabel(header));
            var comboBox = new ComboBox
            {
                ItemsSource = Enum.GetValues<TEnum>()
            };
            comboBox.SetBinding(Selector.SelectedItemProperty, CreateTwoWayBinding(propertyPath));
            panel.Children.Add(comboBox);
        }


        private static void AddCheckBox(StackPanel panel, string header, string propertyPath)
        {
            var checkBox = new CheckBox
            {
                Content = header,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 6, 0, 0)
            };
            checkBox.SetBinding(ToggleButton.IsCheckedProperty, CreateTwoWayBinding(propertyPath));
            panel.Children.Add(checkBox);
        }

        private static void AddReadOnlyText(StackPanel panel, string header, string propertyPath)
        {
            panel.Children.Add(CreateLabel(header));
            var textBlock = new TextBlock
            {
                Foreground = Brushes.WhiteSmoke,
                TextWrapping = TextWrapping.Wrap
            };
            textBlock.SetBinding(TextBlock.TextProperty, new Binding(propertyPath));
            panel.Children.Add(textBlock);
        }

        /// <summary>
        /// 追加"主测量值"说明。
        /// Chinese: 判定比较的是哪一个量，必须与信息面板显示的量一致。平行度/同心度这类名字容易被
        /// 当成 ISO 1101 形位公差，实际只是二维近似指标，所以在编辑器里也明确写出被比较的量。
        /// </summary>
        private static void AppendMeasuredQuantityNote(StackPanel panel, string note)
        {
            var label = CreateLabel(note);
            label.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(label);
        }

        /// <summary>
        /// 追加公差判定编辑组（标称值 + 上/下公差）。
        /// Chinese: 公差值可为空（清空输入即视为未设置）；判定结果在信息面板实时显示。
        /// English: Appends the tolerance judgement editor section (nominal + plus/minus).
        /// </summary>
        private static void AppendToleranceEditor(StackPanel panel, RoiBase roi)
        {
            panel.Children.Add(CreateLabel(UiText.Get("EditorToleranceGroup")));
            AddToleranceField(panel, "EditorToleranceNominal", roi, t => t.Nominal, (t, value) => t.Nominal = value, nonNegative: false);
            AddToleranceField(panel, "EditorTolerancePlus", roi, t => t.TolerancePlus, (t, value) => t.TolerancePlus = value, nonNegative: true);
            AddToleranceField(panel, "EditorToleranceMinus", roi, t => t.ToleranceMinus, (t, value) => t.ToleranceMinus = value, nonNegative: true);
        }

        private static void AddToleranceField(
            StackPanel panel, string field, RoiBase roi,
            Func<MeasurementTolerance, double?> getValue, Action<MeasurementTolerance, double?> setValue, bool nonNegative)
        {
            string header = UiText.Get(field);
            string? invalidText = RoiToleranceDrafts.GetText(roi, field);
            string errorMessage = UiText.Get(nonNegative ? "EditorToleranceInvalidLimit" : "EditorToleranceInvalidNumber");
            panel.Children.Add(CreateLabel(header));
            var textBox = new TextBox
            {
                Text = invalidText ?? (roi.Tolerance is { } existing ? getValue(existing)?.ToString("G", CultureInfo.CurrentCulture) : null) ?? string.Empty
            };
            var error = new TextBlock { Text = errorMessage, TextWrapping = TextWrapping.Wrap };
            error.SetResourceReference(TextBlock.ForegroundProperty, "ViewerErrorBrush");
            void ShowError(bool visible)
            {
                error.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                System.Windows.Automation.AutomationProperties.SetHelpText(textBox, visible ? errorMessage : string.Empty);
            }

            ShowError(invalidText != null);
            System.Windows.Automation.AutomationProperties.SetName(textBox, header);
            textBox.TextChanged += (_, _) =>
            {
                double? value = null;
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    if ((!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) &&
                         !double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) ||
                        !double.IsFinite(parsed) || (nonNegative && parsed < 0))
                    {
                        RoiToleranceDrafts.SetText(roi, field, textBox.Text);
                        ShowError(true);
                        return;
                    }
                    value = parsed;
                }

                RoiToleranceDrafts.SetText(roi, field, null);
                ShowError(false);
                MeasurementTolerance? previous = roi.Tolerance;
                if ((previous is null ? null : getValue(previous)) == value)
                {
                    return;
                }

                // Only replace the persisted value when a field is edited. Opening an editor is read-only.
                MeasurementTolerance updated = previous?.Clone() ?? new MeasurementTolerance();
                setValue(updated, value);
                roi.Tolerance = updated.Nominal.HasValue || updated.TolerancePlus.HasValue || updated.ToleranceMinus.HasValue
                    ? updated
                    : null;
            };
            panel.Children.Add(textBox);
            panel.Children.Add(error);
        }

        private static Binding CreateTwoWayBinding(string propertyPath) => new(propertyPath)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };

    }
}
