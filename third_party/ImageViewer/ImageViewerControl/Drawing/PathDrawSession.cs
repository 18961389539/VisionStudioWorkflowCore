using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.Utils;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 路径式绘制会话的可选行为。
    /// Chinese: 多边形、折线与自由手绘共用同一套路径累积逻辑，差异通过本选项声明。
    /// English: Behavior options for <see cref="PathDrawSession{T}"/>. Polygon, polyline and freehand
    /// share the same point-accumulation logic; their differences are declared here.
    /// </summary>
    public sealed record PathDrawOptions
    {
        /// <summary>提交所需的最少点数。多边形为 3，折线/自由手绘为 2。</summary>
        public int MinimumCommitPoints { get; init; } = 2;

        /// <summary>退出模式时提交（多边形）而非丢弃（折线）。</summary>
        public bool CommitOnModeExit { get; init; }

        /// <summary>提交时标记为闭合（多边形）。</summary>
        public bool CloseOnCommit { get; init; }

        /// <summary>按下右键或双击时结束绘制。</summary>
        public bool FinishOnRightButton { get; init; } = true;

        /// <summary>鼠标抬起时结束绘制（自由手绘）。</summary>
        public bool FinishOnMouseUp { get; init; }

        /// <summary>拖拽过程中持续累积点（自由手绘）。</summary>
        public bool AppendPointsWhileDragging { get; init; }

        /// <summary>首次按下时清空已有点（自由手绘）。</summary>
        public bool ClearPointsOnFirstDown { get; init; }

        /// <summary>绘制"待闭合"预览：橡皮筋 + 闭合虚线 + 闭合高亮手柄（多边形）。</summary>
        public bool ShowClosingPreview { get; init; }

        /// <summary>绘制点数提示文本（多边形）。</summary>
        public bool ShowPointCountInfo { get; init; }
    }

    /// <summary>
    /// 路径式绘制会话：累积任意数量的点，再以工具各自的方式结束。
    /// Chinese: 覆盖多边形（点选累积 + 闭合预览 + 退出时提交）、折线（点选累积 + 橡皮筋预览）
    /// 与自由手绘（拖拽累积 + 抬起结束）。
    /// English: Path draw session: accumulates an arbitrary number of points and finishes in a
    /// tool-specific way. Covers polygon (click accumulation, closing preview, commit on mode exit),
    /// polyline (click accumulation, rubber-band preview) and freehand (drag accumulation, finish on
    /// mouse up).
    /// </summary>
    public sealed class PathDrawSession<T> : IRoiDrawSession
        where T : RoiBase
    {
        private readonly T _roi;
        private readonly Func<T, ObservableCollection<Point>> _getPoints;
        private readonly PathDrawOptions _options;
        private readonly Action<IRoiDrawHost, T>? _beforeCommit;

        private Point? _previewPoint;
        private bool _isCloseCandidate;

        public PathDrawSession(
            T roi,
            Func<T, ObservableCollection<Point>> getPoints,
            PathDrawOptions options,
            Action<IRoiDrawHost, T>? beforeCommit = null)
        {
            _roi = roi ?? throw new ArgumentNullException(nameof(roi));
            _getPoints = getPoints ?? throw new ArgumentNullException(nameof(getPoints));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _beforeCommit = beforeCommit;
        }

        /// <summary>
        /// 尚无任何点时返回 null，避免渲染空图形。
        /// Chinese: 空点集的多边形/折线不应产生任何预览。
        /// English: Returns null while no point has been placed, so an empty polygon or polyline
        /// produces no preview at all.
        /// </summary>
        public RoiBase? ActiveRoi => _getPoints(_roi).Count > 0 ? _roi : null;

        public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled)
            {
                return;
            }

            bool finish = _options.FinishOnRightButton && (e.IsRightButtonPressed || e.ClickCount >= 2);
            if (finish)
            {
                Finish(host);
                return;
            }

            if (!e.IsLeftButtonPressed)
            {
                return;
            }

            if (!host.TryCaptureMouse())
            {
                return;
            }

            Point position = host.SnapPoint(e.RawPosition);
            ObservableCollection<Point> points = _getPoints(_roi);

            if (_options.ShowClosingPreview
                && points.Count >= _options.MinimumCommitPoints
                && ShouldClose(position, points[0], host))
            {
                Finish(host);
                return;
            }

            if (_options.ClearPointsOnFirstDown)
            {
                points.Clear();
            }

            points.Add(position);
            _previewPoint = null;
            host.InvalidateOverlay();
        }

        public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
        {
            ObservableCollection<Point> points = _getPoints(_roi);

            if (_options.AppendPointsWhileDragging)
            {
                Point dragged = host.SnapPoint(e.RawPosition);
                if (Services.RoiGeometryService.ShouldAppendFreehandPolylinePoint(points, dragged))
                {
                    points.Add(dragged);
                    host.InvalidateOverlay();
                }

                return;
            }

            if (points.Count == 0)
            {
                return;
            }

            Point position = host.SnapPoint(e.RawPosition);
            _previewPoint = position;

            if (_options.ShowClosingPreview)
            {
                _isCloseCandidate = points.Count >= _options.MinimumCommitPoints
                    && ShouldClose(position, points[0], host);
                host.SetCursor(_isCloseCandidate ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Pen);
            }

            host.InvalidateOverlay();
        }

        public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (_options.FinishOnMouseUp)
            {
                Finish(host);
            }
        }

        public void OnCaptureLost(IRoiDrawHost host)
        {
            if (_options.FinishOnMouseUp)
            {
                Finish(host);
            }
        }

        public void OnModeExiting(IRoiDrawHost host)
        {
            if (_options.CommitOnModeExit)
            {
                Finish(host);
            }
        }

        public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
        {
            ObservableCollection<Point> points = _getPoints(_roi);
            if (points.Count == 0)
            {
                return;
            }

            Point lastPoint = points[points.Count - 1];

            IReadOnlyList<Point> previewPoints = _previewPoint is Point preview
                ? [..points, preview]
                : [..points];
            string? measurementText = _roi switch
            {
                PolylineRoi when previewPoints.Count > 1 => $"Length: {context.FormatLength(GeometryUtils.PolylineLength(previewPoints))}",
                PolygonRoi when previewPoints.Count >= 3 => $"Area: {context.FormatArea(GeometryUtils.PolygonArea(previewPoints))}",
                _ => null
            };
            if (measurementText != null)
            {
                context.DrawInfoText(
                    measurementText,
                    new Point(lastPoint.X, lastPoint.Y - (context.InfoTextOffset + 18) / context.Scale),
                    System.Windows.Media.Brushes.Orange);
            }

            if (_options.ShowPointCountInfo)
            {
                context.DrawInfoText(
                    $"Points: {points.Count}",
                    new Point(lastPoint.X, lastPoint.Y - context.InfoTextOffset / context.Scale),
                    System.Windows.Media.Brushes.Orange);
            }

            if (_previewPoint is not Point previewPoint)
            {
                return;
            }

            AddDashedLine(context, lastPoint, previewPoint, System.Windows.Media.Brushes.Orange, 2, 2);

            if (!_options.ShowClosingPreview)
            {
                return;
            }

            Point firstPoint = points[0];
            AddDashedLine(
                context,
                previewPoint,
                firstPoint,
                _isCloseCandidate ? System.Windows.Media.Brushes.Yellow : System.Windows.Media.Brushes.Orange,
                1,
                4);

            if (_isCloseCandidate)
            {
                AddCloseHandle(context, firstPoint);
            }
        }

        private static void AddDashedLine(
            RoiRenderContext context,
            Point from,
            Point to,
            System.Windows.Media.Brush stroke,
            double thickness,
            double dashLength)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = stroke,
                StrokeThickness = thickness / context.Scale,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { dashLength, dashLength },
                IsHitTestVisible = false
            };

            context.OverlayCanvas.Children.Add(line);
        }

        private static void AddCloseHandle(RoiRenderContext context, Point position)
        {
            double size = (context.HandleSize + context.PolygonCloseHighlightPadding) / context.Scale;

            var handle = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 215, 0)),
                Stroke = System.Windows.Media.Brushes.Yellow,
                StrokeThickness = 2 / context.Scale,
                IsHitTestVisible = false
            };

            System.Windows.Controls.Canvas.SetLeft(handle, position.X - size / 2);
            System.Windows.Controls.Canvas.SetTop(handle, position.Y - size / 2);
            context.OverlayCanvas.Children.Add(handle);
        }

        private static bool ShouldClose(Point current, Point start, IRoiDrawHost host)
        {
            return Services.RoiGeometryService.ShouldClosePolygon(current, start, host.HitTestTolerance, host.Scale);
        }

        private void Finish(IRoiDrawHost host)
        {
            if (_getPoints(_roi).Count >= _options.MinimumCommitPoints)
            {
                _beforeCommit?.Invoke(host, _roi);
                host.Commit(_roi);
            }

            _previewPoint = null;
            _isCloseCandidate = false;
            host.EndDraw();
        }
    }
}
