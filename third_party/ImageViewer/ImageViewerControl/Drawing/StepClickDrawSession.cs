using System;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 多步点击式绘制会话：按"选择式流程"逐步取点，最后一步提交。
    /// Chinese: 用于角度、圆弧、点到直线/圆距离、平行度、垂直度、同心度等需要依次指定
    /// 几何元素的测量工具。
    /// English: Multi-step click draw session. Each click either starts the ROI or advances a step;
    /// the final step commits. Used by measurements that pick geometry elements in sequence (angle, arc,
    /// point-to-line/circle distance, parallelism, perpendicularity, concentricity).
    /// </summary>
    /// <remarks>
    /// 语义要点：
    /// <list type="bullet">
    /// <item><description><c>createInitial</c> 返回 null 表示拒绝启动（例如未命中可引用的几何元素）。</description></item>
    /// <item><description><c>tryComplete</c> 返回 false 表示本次点击不提交，停留在原步。</description></item>
    /// <item><description>右键丢弃并退出；抬起不参与推进。</description></item>
    /// </list>
    /// English: A null from <c>createInitial</c> refuses to start, a false from <c>tryComplete</c> keeps
    /// the current step, and the right button discards.
    /// </remarks>
    public sealed class StepClickDrawSession<T> : IRoiDrawSession
        where T : RoiBase
    {
        private readonly int _finalStep;
        private readonly Func<IRoiDrawHost, Point, RoiBase?, T?> _createInitial;
        private readonly Action<T, int, Point, RoiBase?>? _updateStep;
        private readonly Func<T, Point, RoiBase?, bool> _tryComplete;
        private readonly Action<IRoiDrawHost, RoiBase?>? _updateIdleCursor;
        private readonly Action<IRoiDrawHost, T, int, Point, RoiBase?>? _updateActivePreview;

        private T? _roi;
        private int _step;

        public StepClickDrawSession(
            int finalStep,
            Func<IRoiDrawHost, Point, RoiBase?, T?> createInitial,
            Func<T, Point, RoiBase?, bool> tryComplete,
            Action<T, int, Point, RoiBase?>? updateStep = null,
            Action<IRoiDrawHost, RoiBase?>? updateIdleCursor = null,
            Action<IRoiDrawHost, T, int, Point, RoiBase?>? updateActivePreview = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(finalStep);

            _finalStep = finalStep;
            _createInitial = createInitial ?? throw new ArgumentNullException(nameof(createInitial));
            _tryComplete = tryComplete ?? throw new ArgumentNullException(nameof(tryComplete));
            _updateStep = updateStep;
            _updateIdleCursor = updateIdleCursor;
            _updateActivePreview = updateActivePreview;
        }

        public RoiBase? ActiveRoi => _roi;

        public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled)
            {
                return;
            }

            if (e.IsRightButtonPressed)
            {
                Reset(host);
                e.Handled = true;
                return;
            }

            if (!e.IsLeftButtonPressed)
            {
                return;
            }

            Point position = host.SnapPoint(e.RawPosition);
            RoiBase? hitRoi = host.HitTest(position);

            if (_step == 0)
            {
                T? created = _createInitial(host, position, hitRoi);
                if (created == null)
                {
                    return;
                }

                if (!host.TryCaptureMouse())
                {
                    return;
                }

                _roi = created;
                _step = _finalStep == 0 ? 0 : 1;
                host.InvalidateOverlay();
                e.Handled = true;
                return;
            }

            if (_roi is not T roi)
            {
                return;
            }

            if (_step < _finalStep)
            {
                _updateStep?.Invoke(roi, _step, position, hitRoi);
                _step++;
                host.InvalidateOverlay();
                e.Handled = true;
                return;
            }

            if (_step == _finalStep)
            {
                if (_tryComplete(roi, position, hitRoi))
                {
                    host.Commit(roi);
                    Reset(host);
                }
                else
                {
                    host.InvalidateOverlay();
                }

                e.Handled = true;
            }
        }

        public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
        {
            Point position = host.SnapPoint(e.RawPosition);
            RoiBase? hitRoi = host.HitTest(position);

            if (_step == 0)
            {
                _updateIdleCursor?.Invoke(host, hitRoi);
                return;
            }

            if (_roi is T roi)
            {
                _updateActivePreview?.Invoke(host, roi, _step, position, hitRoi);
                host.InvalidateOverlay();
            }
        }

        public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
        {
            // 步进式测量在按下时推进，抬起不参与。
        }

        public void OnCaptureLost(IRoiDrawHost host)
        {
            // 既有行为：步进式测量不响应鼠标捕获丢失。
        }

        public void OnModeExiting(IRoiDrawHost host)
        {
            _roi = null;
            _step = 0;
        }

        public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
        {
            // 进行中的 ROI 由宿主经 IRoiRenderer 渲染。
        }

        private void Reset(IRoiDrawHost host)
        {
            _roi = null;
            _step = 0;
            host.EndDraw();
        }
    }
}
