using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Rendering
{
    public sealed class RoiRenderContext
    {
        // 线程安全：ResolveStroke 是公开静态方法，可能被后台渲染或第三方插件并发调用。
        private static readonly ConcurrentDictionary<Color, Brush> StrokeBrushCache = new();

        public RoiRenderContext(
            Canvas overlayCanvas,
            Canvas screenOverlayCanvas,
            Func<Point, Point> imageToScreen,
            double scale,
            double pixelSize,
            string? physicalUnit,
            bool showCaliperScores,
            double handleSize,
            double infoTextOffset,
            double angleArcRadius,
            double pointAnnotationSize,
            double polygonResizeHandlePadding,
            double polygonCloseHighlightPadding)
        {
            OverlayCanvas = overlayCanvas;
            ScreenOverlayCanvas = screenOverlayCanvas;
            ImageToScreen = imageToScreen;
            Scale = scale;
            PixelSize = pixelSize;
            PhysicalUnit = physicalUnit;
            ShowCaliperScores = showCaliperScores;
            HandleSize = handleSize;
            InfoTextOffset = infoTextOffset;
            AngleArcRadius = angleArcRadius;
            PointAnnotationSize = pointAnnotationSize;
            PolygonResizeHandlePadding = polygonResizeHandlePadding;
            PolygonCloseHighlightPadding = polygonCloseHighlightPadding;
        }

        public Canvas OverlayCanvas { get; }
        public Canvas ScreenOverlayCanvas { get; }
        public Func<Point, Point> ImageToScreen { get; }
        public double Scale { get; }
        public double PixelSize { get; }
        public string? PhysicalUnit { get; }
        internal CameraCalibration? Calibration { get; init; }
        public bool ShowCaliperScores { get; }
        public double HandleSize { get; }
        public double InfoTextOffset { get; }
        public double AngleArcRadius { get; }
        public double PointAnnotationSize { get; }
        public double PolygonResizeHandlePadding { get; }

        /// <summary>
        /// 多边形闭合高亮手柄相对普通手柄的额外放大值。
        /// Chinese: 用于绘制"松开即闭合"时的黄色高亮提示。
        /// English: Extra size added to the close highlight handle of a polygon.
        /// </summary>
        public double PolygonCloseHighlightPadding { get; }

        public double ToScreenLength(double imageLength)
        {
            return imageLength * Scale;
        }

        public Point ToScreenPoint(Point imagePoint)
        {
            return ImageToScreen(imagePoint);
        }

        public void DrawLineSegment(
            Point start,
            Point end,
            Brush stroke,
            double thickness,
            DoubleCollection? dashArray = null,
            double opacity = 1.0,
            bool snapToDevicePixels = true)
        {
            Point screenStart = ToScreenPoint(start);
            Point screenEnd = ToScreenPoint(end);
            double screenThickness = Math.Max(1, ToScreenLength(thickness));

            var line = new Line
            {
                X1 = snapToDevicePixels ? AlignCoordinate(screenStart.X, screenThickness) : screenStart.X,
                Y1 = snapToDevicePixels ? AlignCoordinate(screenStart.Y, screenThickness) : screenStart.Y,
                X2 = snapToDevicePixels ? AlignCoordinate(screenEnd.X, screenThickness) : screenEnd.X,
                Y2 = snapToDevicePixels ? AlignCoordinate(screenEnd.Y, screenThickness) : screenEnd.Y,
                Stroke = stroke,
                StrokeThickness = screenThickness,
                Opacity = opacity,
                SnapsToDevicePixels = snapToDevicePixels,
                IsHitTestVisible = false
            };

            if (dashArray != null)
            {
                line.StrokeDashArray = dashArray;
            }

            ScreenOverlayCanvas.Children.Add(line);
        }

        public void DrawRectangleOutline(Point center, double width, double height, double angle, Brush stroke, double thickness, Brush? fill = null)
        {
            double screenWidth = ToScreenLength(width);
            double screenHeight = ToScreenLength(height);
            Point screenCenter = ToScreenPoint(center);

            var shape = new Rectangle
            {
                Width = screenWidth,
                Height = screenHeight,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = Math.Max(1, ToScreenLength(thickness)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                RenderTransform = new RotateTransform(ToScreenAngle(center, angle), screenWidth / 2, screenHeight / 2)
            };

            Canvas.SetLeft(shape, screenCenter.X - screenWidth / 2);
            Canvas.SetTop(shape, screenCenter.Y - screenHeight / 2);
            ScreenOverlayCanvas.Children.Add(shape);
        }

        public void DrawEllipseOutline(Point center, double radiusX, double radiusY, double angle, Brush stroke, double thickness, Brush? fill = null)
        {
            double screenWidth = ToScreenLength(radiusX * 2);
            double screenHeight = ToScreenLength(radiusY * 2);
            Point screenCenter = ToScreenPoint(center);

            var shape = new Ellipse
            {
                Width = screenWidth,
                Height = screenHeight,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = Math.Max(1, ToScreenLength(thickness)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
                RenderTransform = new RotateTransform(ToScreenAngle(center, angle), screenWidth / 2, screenHeight / 2)
            };

            Canvas.SetLeft(shape, screenCenter.X - screenWidth / 2);
            Canvas.SetTop(shape, screenCenter.Y - screenHeight / 2);
            ScreenOverlayCanvas.Children.Add(shape);
        }

        public void DrawPolyline(IReadOnlyList<Point> points, Brush stroke, double thickness, bool closed = false, DoubleCollection? dashArray = null, Brush? fill = null)
        {
            if (points.Count == 0)
            {
                return;
            }

            var screenPoints = new PointCollection(points.Select(ToScreenPoint));
            Shape shape = closed
                ? new Polygon { Points = screenPoints }
                : new Polyline { Points = screenPoints };

            shape.Stroke = stroke;
            if (closed && shape is Polygon polygon)
            {
                polygon.Fill = fill;
            }
            shape.StrokeThickness = Math.Max(1, ToScreenLength(thickness));
            shape.SnapsToDevicePixels = true;
            shape.IsHitTestVisible = false;
            if (dashArray != null)
            {
                shape.StrokeDashArray = dashArray;
            }

            ScreenOverlayCanvas.Children.Add(shape);
        }

        public void DrawDot(Point pos, double size, Brush fill, Brush? stroke = null, double thickness = 1)
        {
            double screenSize = Math.Max(1, ToScreenLength(size));
            Point screenPos = ToScreenPoint(pos);
            var dot = new Ellipse
            {
                Width = screenSize,
                Height = screenSize,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = stroke == null ? 0 : Math.Max(1, ToScreenLength(thickness)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(dot, screenPos.X - screenSize / 2);
            Canvas.SetTop(dot, screenPos.Y - screenSize / 2);
            ScreenOverlayCanvas.Children.Add(dot);
        }

        public static Brush ResolveStroke(Brush? strokeOverride, RoiColor fallback)
        {
            if (strokeOverride != null)
            {
                return strokeOverride;
            }

            Color color = fallback.ToColor();
            if (StrokeBrushCache.TryGetValue(color, out Brush? cachedBrush))
            {
                return cachedBrush;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return StrokeBrushCache.GetOrAdd(color, brush);
        }

        public static Brush CreateTranslucentFill(Brush stroke, byte alpha = 64)
        {
            if (stroke is SolidColorBrush solid)
            {
                Color color = solid.Color;
                var fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                fill.Freeze();
                return fill;
            }

            return new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255));
        }

        public string FormatLength(double pixelLength)
        {
            return $"{pixelLength * PixelSize:F2} {GetDisplayUnit()}";
        }

        public string FormatArea(double pixelArea)
        {
            return $"{pixelArea * PixelSize * PixelSize:F2} {GetDisplayUnit()}²";
        }

        public string FormatPerimeter(double pixelPerimeter)
        {
            return FormatLength(pixelPerimeter);
        }

        internal RoiMeasurementDisplay Measure(RoiBase roi) => new(roi, PixelSize, GetDisplayUnit(), Calibration);

        private double ToScreenAngle(Point center, double angle)
        {
            Vector direction = ToScreenPoint(PointOnCircle(center, 1, angle)) - ToScreenPoint(center);
            return Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        }

        private static Point PointOnCircle(Point center, double radius, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            return center + new Vector(radius * Math.Cos(radians), radius * Math.Sin(radians));
        }

        public void DrawHandle(Point pos, ResizeHandle handleType, double size, bool isRect = true, Brush? stroke = null)
        {
            Shape handle = isRect ? new Rectangle() : new Ellipse();
            double screenSize = Math.Max(1, ToScreenLength(size));
            Point screenPos = ToScreenPoint(pos);
            handle.Width = screenSize;
            handle.Height = screenSize;
            handle.Fill = Brushes.White;
            handle.Stroke = stroke ?? Brushes.Blue;
            handle.StrokeThickness = Math.Max(1, ToScreenLength(1 / Scale));
            handle.Tag = handleType;
            handle.SnapsToDevicePixels = true;
            handle.IsHitTestVisible = false;

            Canvas.SetLeft(handle, screenPos.X - screenSize / 2);
            Canvas.SetTop(handle, screenPos.Y - screenSize / 2);
            ScreenOverlayCanvas.Children.Add(handle);
        }

        public void DrawInfoText(string text, Point pos, Brush brush, bool centerAlign = false)
        {
            TextBlock textBlock = CreateInfoTextBlock(text, brush);

            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point screenPos = ImageToScreen(pos);
            double offsetX = centerAlign ? -textBlock.DesiredSize.Width / 2 : 0;
            double offsetY = centerAlign ? -textBlock.DesiredSize.Height / 2 : 0;

            Canvas.SetLeft(textBlock, Math.Round(screenPos.X + offsetX));
            Canvas.SetTop(textBlock, Math.Round(screenPos.Y + offsetY));
            ScreenOverlayCanvas.Children.Add(textBlock);
        }

        /// <summary>
        /// Measures the screen-space size used by an overlay label.
        /// Chinese: 用于计算标签与测量线之间的安全间距，避免文字框覆盖测量线。
        /// English: Returns the screen-space size of an overlay label so callers can keep it clear of a measurement line.
        /// </summary>
        public static Size MeasureInfoText(string text)
        {
            TextBlock textBlock = CreateInfoTextBlock(text, Brushes.White);
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return textBlock.DesiredSize;
        }

        private static TextBlock CreateInfoTextBlock(string text, Brush brush)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                Padding = new Thickness(3, 2, 3, 2),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(textBlock, TextRenderingMode.ClearType);
            return textBlock;
        }

        public void DrawAngleArc(Point center, Point p1, Point p2, double radius, Brush brush)
        {
            Point screenCenter = ToScreenPoint(center);
            Point screenP1 = ToScreenPoint(p1);
            Point screenP2 = ToScreenPoint(p2);

            Vector v1 = screenP1 - screenCenter;
            Vector v2 = screenP2 - screenCenter;
            v1.Normalize();
            v2.Normalize();

            Point startPoint = screenCenter + v1 * radius;
            Point endPoint = screenCenter + v2 * radius;
            double crossProduct = v1.X * v2.Y - v1.Y * v2.X;
            SweepDirection sweepDir = crossProduct > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            var path = new Path
            {
                Stroke = brush,
                StrokeThickness = Math.Max(1, ToScreenLength(1 / Scale)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(startPoint, false, false);
                ctx.ArcTo(endPoint, new Size(radius, radius), 0, false, sweepDir, true, false);
            }

            path.Data = geometry;
            ScreenOverlayCanvas.Children.Add(path);
        }

        public void DrawRotationHandle(Point topCenter, RotateTransform rotateTransform, double handleSize)
        {
            Point rotationHandlePoint = new(topCenter.X, topCenter.Y - InfoTextOffset / Scale);
            Point rotatedTopCenter = rotateTransform.Transform(topCenter);
            Point rotatedRotationPoint = rotateTransform.Transform(rotationHandlePoint);
            DrawLineSegment(rotatedTopCenter, rotatedRotationPoint, Brushes.Blue, 1 / Scale);
            DrawHandle(rotatedRotationPoint, ResizeHandle.Rotation, handleSize, false);
        }

        private static double AlignCoordinate(double value, double thickness)
        {
            return thickness <= 1.5 ? Math.Round(value) + 0.5 : Math.Round(value);
        }

        public static Dictionary<ResizeHandle, Point> CreateBoxHandlePositions(Point center, double halfWidth, double halfHeight)
        {
            return new Dictionary<ResizeHandle, Point>
            {
                { ResizeHandle.TopLeft, new Point(center.X - halfWidth, center.Y - halfHeight) },
                { ResizeHandle.TopCenter, new Point(center.X, center.Y - halfHeight) },
                { ResizeHandle.TopRight, new Point(center.X + halfWidth, center.Y - halfHeight) },
                { ResizeHandle.MiddleRight, new Point(center.X + halfWidth, center.Y) },
                { ResizeHandle.BottomRight, new Point(center.X + halfWidth, center.Y + halfHeight) },
                { ResizeHandle.BottomCenter, new Point(center.X, center.Y + halfHeight) },
                { ResizeHandle.BottomLeft, new Point(center.X - halfWidth, center.Y + halfHeight) },
                { ResizeHandle.MiddleLeft, new Point(center.X - halfWidth, center.Y) }
            };
        }

        public void DrawArc(Point center, double radius, double startAngle, double sweepAngle, Brush stroke, double thickness)
        {
            if (radius <= 0) return;

            double screenRadius = ToScreenLength(radius);
            Point startPoint = ToScreenPoint(PointOnCircle(center, radius, startAngle));
            Point endPoint = ToScreenPoint(PointOnCircle(center, radius, startAngle + sweepAngle));
            Point screenCenter = ToScreenPoint(center);
            Vector xAxis = ToScreenPoint(center + new Vector(1, 0)) - screenCenter;
            Vector yAxis = ToScreenPoint(center + new Vector(0, 1)) - screenCenter;
            bool mirrored = Vector.CrossProduct(xAxis, yAxis) < 0;

            bool isLargeArc = Math.Abs(sweepAngle) > 180;
            SweepDirection sweepDir = (sweepAngle >= 0) != mirrored ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            var path = new Path
            {
                Stroke = stroke,
                StrokeThickness = Math.Max(1, ToScreenLength(thickness)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(startPoint, false, false);
                ctx.ArcTo(endPoint, new Size(screenRadius, screenRadius), 0, isLargeArc, sweepDir, true, false);
            }

            path.Data = geometry;
            ScreenOverlayCanvas.Children.Add(path);
        }

        public void DrawCircleOutline(Point center, double radius, Brush stroke, double thickness, Brush? fill = null)
        {
            double screenDiameter = ToScreenLength(radius * 2);
            Point screenCenter = ToScreenPoint(center);

            var shape = new Ellipse
            {
                Width = screenDiameter,
                Height = screenDiameter,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = Math.Max(1, ToScreenLength(thickness)),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(shape, screenCenter.X - screenDiameter / 2);
            Canvas.SetTop(shape, screenCenter.Y - screenDiameter / 2);
            ScreenOverlayCanvas.Children.Add(shape);
        }

        private string GetDisplayUnit()
        {
            return string.IsNullOrWhiteSpace(PhysicalUnit) ? "px" : PhysicalUnit;
        }
    }
}
