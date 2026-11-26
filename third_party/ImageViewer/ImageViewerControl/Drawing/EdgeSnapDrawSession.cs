using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 自动边缘吸附点的交互会话：移动时在后台搜索候选，点击时提交同一候选结果。
    /// Chinese: 同一时刻最多一个后台搜索在飞，指针移动只记录最新位置；完成后若指针已移动，
    /// 丢弃陈旧结果并补算最新位置，避免高频移动把卡尺搜索堆成队列压垮 UI 线程。
    /// English: At most one background search runs at a time; pointer moves only record the latest
    /// position. A result for an older position is discarded and the latest position is evaluated
    /// instead, so preview stays consistent without queue buildup.
    /// </summary>
    public sealed class EdgeSnapDrawSession : IRoiDrawSession
    {
        private const double SamePositionTolerance = 1.5;

        private readonly Func<Point, double, double, RoiBase?> _createAt;
        private Point _previewPoint;
        private double _previewScore;
        private double _previewConfidence;
        private Point _previewRawPosition;
        private bool _hasPreview;
        private Point _latestPosition;
        private bool _hasLatestPosition;
        private SnapRequest? _activeRequest;
        private int _version;

        public EdgeSnapDrawSession(Func<Point, double, double, RoiBase?> createAt)
        {
            _createAt = createAt ?? throw new ArgumentNullException(nameof(createAt));
        }

        public RoiBase? ActiveRoi => null;

        public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled || !e.IsLeftButtonPressed)
            {
                return;
            }

            CancelActiveRequest();
            if (!TryResolveCommitCandidate(host, e.RawPosition, out Point point, out double score, out double confidence))
            {
                return;
            }

            RoiBase? roi = _createAt(point, score, confidence);
            if (roi == null)
            {
                return;
            }

            host.Commit(roi);
            host.EndDraw();
            e.Handled = true;
        }

        public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled)
            {
                return;
            }

            _latestPosition = e.RawPosition;
            _hasLatestPosition = true;
            if (_activeRequest is null)
            {
                StartRequest(host, e.RawPosition);
            }
        }

        public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
        {
        }

        public void OnCaptureLost(IRoiDrawHost host) => Reset(host, invalidate: true);

        public void OnModeExiting(IRoiDrawHost host) => Reset(host, invalidate: false);

        public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
        {
            if (!_hasPreview)
            {
                return;
            }

            Brush brush = _previewConfidence >= 0.35 ? Brushes.LimeGreen : Brushes.Gold;
            double arm = 7 / context.Scale;
            context.DrawLineSegment(
                new Point(_previewPoint.X - arm, _previewPoint.Y),
                new Point(_previewPoint.X + arm, _previewPoint.Y),
                brush,
                1.5 / context.Scale);
            context.DrawLineSegment(
                new Point(_previewPoint.X, _previewPoint.Y - arm),
                new Point(_previewPoint.X, _previewPoint.Y + arm),
                brush,
                1.5 / context.Scale);
            context.DrawDot(_previewPoint, 3 / context.Scale, brush);
            context.DrawInfoText(
                $"Edge {_previewConfidence * 100:F0}%",
                new Point(_previewPoint.X + 8 / context.Scale, _previewPoint.Y - 8 / context.Scale),
                brush,
                true);
        }

        private bool TryResolveCommitCandidate(
            IRoiDrawHost host, Point rawPosition, out Point point, out double score, out double confidence)
        {
            if (_hasPreview && IsSamePosition(_previewRawPosition, rawPosition))
            {
                point = _previewPoint;
                score = _previewScore;
                confidence = _previewConfidence;
                return true;
            }

            // 点击必须落在点击位置上，即使后台预览已过期或仍在计算：保留一次同步搜索，
            // 保证“十字线显示哪里、点击就提交哪里”的语义不因异步化而改变。
            return host.TrySnapPointToEdge(rawPosition, out point, out score, out confidence);
        }

        private void StartRequest(IRoiDrawHost host, Point position)
        {
            var request = new SnapRequest(_version, position, new CancellationTokenSource());
            _activeRequest = request;
            _ = EvaluateAsync(host, request);
        }

        private async Task EvaluateAsync(IRoiDrawHost host, SnapRequest request)
        {
            EdgeSnapResult result;
            try
            {
                result = await host.SnapPointToEdgeAsync(request.Position, request.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                request.Cancellation.Dispose();
                return;
            }
            catch (Exception)
            {
                // 预览失败不能让绘制会话崩溃；真正的提交仍由点击时的同步搜索保证。
                result = EdgeSnapResult.NotFound;
            }

            if (request.Cancellation.IsCancellationRequested)
            {
                request.Cancellation.Dispose();
                return;
            }

            host.PostToUiThread(() => CompleteRequest(host, request, result));
        }

        private void CompleteRequest(IRoiDrawHost host, SnapRequest request, EdgeSnapResult result)
        {
            if (!ReferenceEquals(_activeRequest, request))
            {
                request.Cancellation.Dispose();
                return;
            }

            _activeRequest = null;
            request.Cancellation.Dispose();

            if (request.Version != _version)
            {
                return;
            }

            if (_hasLatestPosition && !IsSamePosition(request.Position, _latestPosition))
            {
                StartRequest(host, _latestPosition);
                return;
            }

            ApplyPreview(host, result);
        }

        private void ApplyPreview(IRoiDrawHost host, EdgeSnapResult result)
        {
            _previewRawPosition = _latestPosition;
            if (result.Found)
            {
                _previewPoint = result.Point;
                _previewScore = result.Score;
                _previewConfidence = result.Confidence;
                _hasPreview = true;
                host.SetCursor(Cursors.Hand);
            }
            else
            {
                _hasPreview = false;
                host.SetCursor(Cursors.Cross);
            }

            host.InvalidateOverlay();
        }

        private void Reset(IRoiDrawHost host, bool invalidate)
        {
            _version++;
            CancelActiveRequest();
            _hasPreview = false;
            _hasLatestPosition = false;
            if (invalidate)
            {
                host.InvalidateOverlay();
            }
        }

        private void CancelActiveRequest()
        {
            SnapRequest? request = _activeRequest;
            _activeRequest = null;
            if (request is null)
            {
                return;
            }

            request.Cancellation.Cancel();
            request.Cancellation.Dispose();
        }

        private static bool IsSamePosition(Point left, Point right)
        {
            double dx = left.X - right.X;
            double dy = left.Y - right.Y;
            return (dx * dx) + (dy * dy) <= SamePositionTolerance * SamePositionTolerance;
        }

        private sealed class SnapRequest
        {
            public SnapRequest(int version, Point position, CancellationTokenSource cancellation)
            {
                Version = version;
                Position = position;
                Cancellation = cancellation;
            }

            public int Version { get; }

            public Point Position { get; }

            public CancellationTokenSource Cancellation { get; }
        }
    }
}