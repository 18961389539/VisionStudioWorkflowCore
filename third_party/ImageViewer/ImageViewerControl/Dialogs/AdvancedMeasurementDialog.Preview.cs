using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Measurements;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using Microsoft.Win32;

namespace ImageViewer.Dialogs;

internal sealed partial class AdvancedMeasurementDialog
{
    private void DrawWitness(Canvas canvas, EvaluationRow row)
    {
        canvas.Children.Clear();
        var layers = new List<GeometryLayer>();
        foreach (Guid sourceId in row.Definition.Sources)
        {
            RoiBase? roi = _observedRois.FirstOrDefault(item => item.MeasurementId == sourceId);
            if (roi is null)
            {
                continue;
            }

            IReadOnlyList<PointD> sourcePoints = MeasurementPreviewGeometry.GetSourceOutline(roi);
            if (sourcePoints.Count > 0)
            {
                layers.Add(new GeometryLayer(sourcePoints.Select(point => ConvertSourcePoint(point, row.Definition)).ToArray(), Brushes.DimGray, roi is PolygonRoi { IsClosed: true } or CircleRoi or EllipseRoi or RotatedRect));
            }
        }

        foreach (PointD[] sourceContour in row.Evaluation.SourceContours)
        {
            if (sourceContour.Length > 0)
            {
                layers.Add(new GeometryLayer(
                    sourceContour.Select(point => ConvertPhysicalSourcePoint(point, row.Definition)).ToArray(),
                    Brushes.OrangeRed,
                    Closed: true));
            }
        }

        if (row.Definition.Kind == AdvancedMeasurementKind.GapProfile)
        {
            for (int i = 0; i + 1 < row.Evaluation.Geometry.Length; i += 2)
            {
                layers.Add(new GeometryLayer([row.Evaluation.Geometry[i], row.Evaluation.Geometry[i + 1]], Brushes.DodgerBlue, Closed: false));
            }
        }
        else if (row.Evaluation.Geometry.Length > 0)
        {
            bool closedGeometry = row.Definition.Kind is AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.ShapeMetrics;
            layers.Add(new GeometryLayer(row.Evaluation.Geometry, Brushes.DodgerBlue, closedGeometry));
        }

        if (layers.Count == 0)
        {
            AddCanvasHint(canvas, UiText.Get("AdvancedMeasurementNoPreview"));
            return;
        }

        canvas.SizeChanged -= OnCanvasSizeChanged;
        canvas.SizeChanged += OnCanvasSizeChanged;
        DrawGeometryLayers(canvas, layers);
    }

    private void DrawSamples(Canvas canvas, IReadOnlyList<double> stations, IReadOnlyList<double> samples)
    {
        canvas.SizeChanged -= OnCanvasSizeChanged;
        canvas.SizeChanged += OnCanvasSizeChanged;
        canvas.Children.Clear();
        if (samples.Count == 0)
        {
            AddCanvasHint(canvas, UiText.Get("AdvancedMeasurementNoSamples"));
            return;
        }

        double[] xValues = stations.Count == samples.Count
            ? stations.ToArray()
            : Enumerable.Range(0, samples.Count).Select(index => (double)index).ToArray();
        double minX = xValues.Min();
        double maxX = xValues.Max();
        double minY = samples.Min();
        double maxY = samples.Max();
        var points = new PointD[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            points[i] = new PointD(xValues[i], samples[i]);
        }

        string unit = _evaluationRows.FirstOrDefault(row => ReferenceEquals(row.Evaluation.Samples, samples))?.Evaluation.Metrics.FirstOrDefault()?.Unit ?? _getUnit();
        DrawPolyline(canvas, points, Brushes.MediumSeaGreen, close: false, minX, maxX, minY, maxY,
            points.Select((point, index) => UiText.Format("AdvancedMeasurementSampleTooltip", index + 1, Format(point.X), Format(point.Y), unit)).ToArray());
        var stationLabel = new TextBlock
        {
            Text = stations.Count == samples.Count
                ? UiText.Format("AdvancedMeasurementSampleStationAxis", Format(minX), Format(maxX), _getUnit())
                : UiText.Format("AdvancedMeasurementSampleIndexAxis", Format(minX + 1), Format(maxX + 1)),
            Foreground = SystemColors.GrayTextBrush,
            FontSize = 10,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(stationLabel, 4);
        Canvas.SetTop(stationLabel, 2);
        canvas.Children.Add(stationLabel);
        var valueLabel = new TextBlock
        {
            Text = UiText.Format("AdvancedMeasurementSampleValueAxis", Format(minY), Format(maxY), unit),
            Foreground = SystemColors.GrayTextBrush,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(valueLabel, 4);
        Canvas.SetTop(valueLabel, 16);
        canvas.Children.Add(valueLabel);
        if (samples.Count == 1)
        {
            AddCanvasHint(canvas, Format(samples[0]));
        }
    }

    private void DrawPolyline(Canvas canvas, IReadOnlyList<PointD> points, Brush stroke, bool close, double? minX = null, double? maxX = null, double? minY = null, double? maxY = null, IReadOnlyList<string>? markerTooltips = null)
    {
        double canvasWidth = Math.Max(canvas.ActualWidth, 120);
        double canvasHeight = Math.Max(canvas.ActualHeight, 100);
        double lowX = minX ?? points.Min(point => point.X);
        double highX = maxX ?? points.Max(point => point.X);
        double lowY = minY ?? points.Min(point => point.Y);
        double highY = maxY ?? points.Max(point => point.Y);
        // Profile axes have independent units/ranges; preserving geometry aspect would hide small deviations.
        double drawingWidth = canvasWidth - 32;
        double drawingHeight = canvasHeight - 64;
        Point Map(PointD point) => new(
            highX == lowX ? canvasWidth / 2 : 16 + (point.X - lowX) / (highX - lowX) * drawingWidth,
            highY == lowY ? 32 + drawingHeight / 2 : 32 + (highY - point.Y) / (highY - lowY) * drawingHeight);

        var polyline = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Points = new PointCollection(points.Select(Map))
        };
        if (close && points.Count > 2)
        {
            polyline.Points.Add(Map(points[0]));
        }

        canvas.Children.Add(polyline);
        for (int i = 0; i < points.Count; i++)
        {
            Point point = Map(points[i]);
            var marker = new Ellipse { Width = 5, Height = 5, Fill = stroke };
            if (markerTooltips is not null && i < markerTooltips.Count)
            {
                ToolTipService.SetToolTip(marker, markerTooltips[i]);
            }

            Canvas.SetLeft(marker, point.X - 2.5);
            Canvas.SetTop(marker, point.Y - 2.5);
            canvas.Children.Add(marker);
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_resultsList.SelectedItem is EvaluationRow row)
        {
            if (ReferenceEquals(sender, _witnessCanvas))
            {
                DrawWitness(_witnessCanvas, row);
            }
            else if (ReferenceEquals(sender, _samplesCanvas))
            {
                DrawSamples(_samplesCanvas, row.Evaluation.Stations, row.Evaluation.Samples);
            }
        }
    }

    private PointD ConvertSourcePoint(PointD point, AdvancedMeasurementDefinition definition)
    {
        PointD corrected = _getCalibration()?.UndistortPoint(point) ?? point;
        var physical = new PointD(corrected.X * _getPixelSize(), corrected.Y * _getPixelSize());
        return ConvertPhysicalSourcePoint(physical, definition);
    }

    private PointD ConvertPhysicalSourcePoint(PointD physical, AdvancedMeasurementDefinition definition)
    {
        if (definition.Kind == AdvancedMeasurementKind.Position && definition.NominalInWorkpieceCoordinates && _getWorkpiece?.Invoke() is { IsDefined: true } workpiece)
        {
            PointD origin = ConvertImagePoint(workpiece.ToImage(new PointD(0, 0)));
            PointD axis = ConvertImagePoint(workpiece.ToImage(new PointD(1, 0)));
            physical = RigidTransform2D.FromDatum(origin, axis - origin).Transform(physical);
        }

        return physical;
    }

    private PointD ConvertImagePoint(PointD point)
    {
        PointD corrected = _getCalibration()?.UndistortPoint(point) ?? point;
        return new PointD(corrected.X * _getPixelSize(), corrected.Y * _getPixelSize());
    }

    private static void DrawGeometryLayers(Canvas canvas, IReadOnlyList<GeometryLayer> layers)
    {
        PointD[] points = layers.SelectMany(layer => layer.Points).ToArray();
        double canvasWidth = Math.Max(canvas.ActualWidth, 120);
        double canvasHeight = Math.Max(canvas.ActualHeight, 100);
        double lowX = points.Min(point => point.X);
        double highX = points.Max(point => point.X);
        double lowY = points.Min(point => point.Y);
        double highY = points.Max(point => point.Y);
        double scaleX = (canvasWidth - 24) / Math.Max(highX - lowX, 1e-9);
        double scaleY = (canvasHeight - 24) / Math.Max(highY - lowY, 1e-9);
        double scale = Math.Min(scaleX, scaleY);
        double drawingWidth = (highX - lowX) * scale;
        double drawingHeight = (highY - lowY) * scale;
        double left = (canvasWidth - drawingWidth) / 2;
        double top = (canvasHeight - drawingHeight) / 2;
        Point Map(PointD point) => new(left + ((point.X - lowX) * scale), canvasHeight - top - ((point.Y - lowY) * scale));

        foreach (GeometryLayer layer in layers)
        {
            if (layer.Points.Count == 0)
            {
                continue;
            }

            var polyline = new Polyline
            {
                Stroke = layer.Stroke,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection(layer.Points.Select(Map))
            };
            if (layer.Closed && layer.Points.Count > 2)
            {
                polyline.Points.Add(Map(layer.Points[0]));
            }

            canvas.Children.Add(polyline);
        }
    }

}
