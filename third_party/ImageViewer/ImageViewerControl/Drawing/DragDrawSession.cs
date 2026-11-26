using System;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 拖拽式绘制会话：按下起点 → 移动改几何 → 抬起校验并提交。
    /// Chinese: 覆盖矩形、椭圆、圆、圆环、卡尺与直线测量等"按住拖出尺寸"的工具。
    /// English: Drag-to-size draw session: press an origin, drag to resize, release to validate and
    /// commit. Covers rectangle/ellipse/circle/ring/caliper/line-measure style tools.
    /// </summary>
    /// <remarks>
    /// 确认点击（<paramref name="finalStage"/> &gt; 0）用于圆环：第一次抬起只进入确认等待，不提交。
    /// Chinese: 进入确认等待时冻结几何但保持鼠标捕获，再抬起一次（点击确认）才提交；右键可取消。
    /// 注意：这里不能主动释放捕获 —— 宿主会把它当成 LostMouseCapture 并取消整次绘制。
    /// English: A non-final stage is a confirmation wait: it freezes the geometry while keeping mouse
    /// capture, and the next release commits. Capture must not be released here, because the host
    /// treats a released capture as LostMouseCapture and cancels the whole draw operation.
    /// </remarks>
    public sealed class DragDrawSession<T> : IRoiDrawSession
        where T : RoiBase
    {
        private readonly Func<IRoiDrawHost, Point, T> _createInitial;
        private readonly Action<IRoiDrawHost, T, Point, Point> _applyDrag;
        private readonly Func<IRoiDrawHost, T, bool>? _shouldCommit;
        private readonly Action<IRoiDrawHost, T>? _beforeCommit;
        private readonly int _finalStage;
        private readonly Func<IRoiDrawHost, T, bool>? _shouldAdvanceStage;
        // Kept for constructor compatibility: the ring now maintains its inner radius while dragging,
        // so no built-in tool supplies this callback any more.
        private readonly Action<IRoiDrawHost, T, int>? _onStageAdvanced;
        private readonly bool _cancelOnRightButton;

        private T? _roi;
        private Point _origin;
        private int _stage;
        private bool _geometryFrozen;

        public DragDrawSession(
            Func<IRoiDrawHost, Point, T> createInitial,
            Action<IRoiDrawHost, T, Point, Point> applyDrag,
            Func<IRoiDrawHost, T, bool>? shouldCommit = null,
            Action<IRoiDrawHost, T>? beforeCommit = null,
            int finalStage = 0,
            Func<IRoiDrawHost, T, bool>? shouldAdvanceStage = null,
            Action<IRoiDrawHost, T, int>? onStageAdvanced = null,
            bool cancelOnRightButton = false)
        {
            _createInitial = createInitial ?? throw new ArgumentNullException(nameof(createInitial));
            _applyDrag = applyDrag ?? throw new ArgumentNullException(nameof(applyDrag));
            _shouldCommit = shouldCommit;
            _beforeCommit = beforeCommit;
            _finalStage = finalStage;
            _shouldAdvanceStage = shouldAdvanceStage;
            _onStageAdvanced = onStageAdvanced;
            _cancelOnRightButton = cancelOnRightButton;
        }

        public RoiBase? ActiveRoi => _roi;

        public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled)
            {
                return;
            }

            if (_cancelOnRightButton && e.IsRightButtonPressed)
            {
                _roi = null;
                host.EndDraw();
                return;
            }

            if (!e.IsLeftButtonPressed || _roi != null)
            {
                return;
            }

            if (!host.TryCaptureMouse())
            {
                return;
            }

            _origin = host.SnapPoint(e.RawPosition);
            _roi = _createInitial(host, _origin);
            _stage = 0;
            _geometryFrozen = false;
        }

        public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (_roi is not T roi || _geometryFrozen)
            {
                return;
            }

            Point current = host.SnapPoint(e.RawPosition);
            _applyDrag(host, roi, _origin, current);
            host.InvalidateOverlay();
        }

        public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (_roi is not T roi)
            {
                // 没有进行中的 ROI 时也离开当前模式（例如按下未成功创建图形后的抬起）。
                host.EndDraw();
                return;
            }

            if (_stage < _finalStage)
            {
                AdvanceStage(host, roi);
                return;
            }

            if (_shouldCommit?.Invoke(host, roi) ?? true)
            {
                _beforeCommit?.Invoke(host, roi);
                host.Commit(roi);
            }

            _roi = null;
            host.EndDraw();
        }

        public void OnCaptureLost(IRoiDrawHost host)
        {
            if (_roi == null)
            {
                return;
            }

            _roi = null;
            host.EndDraw();
        }

        public void OnModeExiting(IRoiDrawHost host)
        {
            _roi = null;
            _geometryFrozen = false;
        }

        public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
        {
            // 确认等待（几何已冻结）：提示再点一次落地，右键取消。
            if (_roi is null || !_geometryFrozen)
            {
                return;
            }

            var anchor = new Point(_origin.X, _origin.Y - (context.HandleSize / context.Scale));
            context.DrawInfoText(UiText.Get("RoiDrawClickToConfirm"), anchor, Brushes.White, centerAlign: true);
        }

        private void AdvanceStage(IRoiDrawHost host, T roi)
        {
            if (!(_shouldAdvanceStage?.Invoke(host, roi) ?? true))
            {
                _roi = null;
                host.EndDraw();
                return;
            }

            _stage++;
            _onStageAdvanced?.Invoke(host, roi, _stage);

            // 保持鼠标捕获：主动 ReleaseMouseCapture 会同步触发宿主的 LostMouseCapture，
            // 进而调用 OnCaptureLost 把正在等待确认的几何清掉（表现为松手即消失）。
            // 这里只冻结几何，捕获留给确认点击或 EndDraw 统一释放。
            _geometryFrozen = true;
            host.InvalidateOverlay();
        }
    }
}
