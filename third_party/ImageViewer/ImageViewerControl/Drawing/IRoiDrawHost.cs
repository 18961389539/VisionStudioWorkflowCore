using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 绘制宿主：绘制会话与 ImageViewer 控件之间的适配面。
    /// Chinese: 让会话不直接依赖 ImageViewer，从而可脱离 WPF 事件单独单测。
    /// English: Adapter surface between a draw session and the ImageViewer control. Lets sessions
    /// avoid depending on the control directly, so they can be unit tested without WPF input events.
    /// </summary>
    public interface IRoiDrawHost
    {
        ImageViewerViewModel ViewModel { get; }

        double Scale { get; }

        double MinimumDrawableSize { get; }

        /// <summary>
        /// 判定"有效长度"的最小值。
        /// Chinese: 低于该长度时卡尺类工具不再做边缘检测，避免噪声结果。
        /// English: Minimum length for a drag to be considered meaningful. Below it, caliper tools skip
        /// edge detection to avoid noise-driven results.
        /// </summary>
        double MinimumLineLength { get; }

        double HitTestTolerance { get; }

        /// <summary>
        /// 按当前网格设置吸附坐标。
        /// Chinese: 多数工具使用吸附坐标；外部落点工具使用原始坐标。
        /// English: Applies the current snap-to-grid setting. Most tools snap; external placement
        /// tools use the raw coordinate.
        /// </summary>
        Point SnapPoint(Point rawImagePoint);

        RoiBase? HitTest(Point point);

        bool TryCaptureMouse();

        void ReleaseMouseCapture();

        void SetCursor(Cursor cursor);

        /// <summary>
        /// 请求重绘 ROI 图层（含进行中的预览）。
        /// Chinese: 对应控件的 DrawRois()，可能被合并延后执行。
        /// English: Requests an overlay redraw; may be coalesced and deferred by the control.
        /// </summary>
        void InvalidateOverlay();

        /// <summary>
        /// 请求一段文本输入。
        /// Chinese: 返回 null 或空白表示用户取消，会话应放弃本次绘制并留在当前模式。
        /// English: Requests a text input from the user. Returning null or whitespace means the user
        /// cancelled; the session should abandon the draw and stay in the current mode.
        /// </summary>
        string? RequestTextInput(string message, string defaultValue);

        /// <summary>
        /// 触发指定 ROI 的检测/分析（斑块、卡尺类）。
        /// Chinese: 必须在 <see cref="Commit"/> 之前调用，否则检测结果不会进入撤销命令捕获的状态。
        /// English: Runs detection/analysis for the ROI. Must be called before <see cref="Commit"/>,
        /// otherwise the detected geometry will not be captured by the undo command's state snapshot.
        /// </summary>
        bool TryApplyAnalysis(RoiBase roi);

        /// <summary>
        /// 在指定位置自动搜索并拟合圆。
        /// Chinese: 使用当前分析图像完成一次单击式自动圆测量；失败时返回 false。
        /// English: Performs a one-click automatic circle search on the current analysis image.
        /// Returns false when no reliable circle can be found.
        /// </summary>
        bool TryCreateAutomaticCircle(Point seed, out CircularCaliperMeasureRoi roi)
        {
            roi = null!;
            return false;
        }

        /// <summary>
        /// 在点击点附近搜索最强图像边缘，并返回亚像素位置及质量指标。
        /// </summary>
        bool TrySnapPointToEdge(Point seed, out Point snapped, out double score, out double confidence)
        {
            snapped = default;
            score = 0;
            confidence = 0;
            return false;
        }

        /// <summary>
        /// 在后台完成一次边缘吸附搜索。
        /// Chinese: 默认实现同步转调 <see cref="TrySnapPointToEdge"/>，旧宿主无需修改；
        /// 真实控件覆写为后台计算，避免高频鼠标移动把卡尺搜索压在 UI 线程上。
        /// English: Default implementation forwards to the synchronous probe so existing hosts keep
        /// working; the real control overrides it to keep pointer-move feedback off the UI thread.
        /// </summary>
        Task<EdgeSnapResult> SnapPointToEdgeAsync(Point seed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                TrySnapPointToEdge(seed, out Point snapped, out double score, out double confidence)
                    ? new EdgeSnapResult(true, snapped, score, confidence)
                    : EdgeSnapResult.NotFound);
        }

        /// <summary>
        /// 把后台完成回调切回 UI 线程。
        /// Chinese: 默认直接执行，便于纯单测；真实控件覆写为 Dispatcher 投递。
        /// English: Default runs inline for plain unit tests; the control posts through its dispatcher.
        /// </summary>
        void PostToUiThread(Action action) => action();

        /// <summary>
        /// 把 ROI 提交进集合（经撤销栈）。
        /// Chinese: 对应控件的 CommitRoi()。
        /// English: Commits the ROI into its collection through the undo stack.
        /// </summary>
        void Commit(RoiBase roi);

        /// <summary>
        /// 结束本次绘制：释放鼠标捕获、离开交互模式并刷新。
        /// Chinese: 必须释放捕获，否则后续平移/选择/编辑会失效。
        /// English: Ends the draw: releases mouse capture, leaves the interaction mode and refreshes.
        /// Releasing capture is mandatory, otherwise later pan/select/edit interactions break.
        /// </summary>
        void EndDraw();
    }
}
