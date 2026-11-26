using System.Windows.Media.Imaging;
using VisionStudio.Abstractions;

namespace VisionStudio.Wpf.Controls.ImageViewer;

/// <summary>
/// 图像浏览器适配契约：隔离 <c>18961389539/ImageViewer</c>（<c>ImageViewerControl</c>）与业务 ViewModel，
/// 确保不修改第三方图像控件源码的前提下，同时支持外部控件租约集成与离线内建渲染回退。
/// </summary>
public interface IVisionImageViewerAdapter
{
    /// <summary>当前是否启用了外部 <c>18961389539/ImageViewer</c> 控件。</summary>
    bool IsExternalImageViewerActive { get; }

    /// <summary>设置当前显示图像，可选自动适配视口。</summary>
    void SetImage(BitmapSource? image, string? caption = null, bool fitToView = true);

    /// <summary>设置叠加标注集合（支持检测框、中心点、拟合圆、卡尺边缘、像素区域等）。</summary>
    void SetOverlays(IReadOnlyList<VisionOverlay>? overlays);

    /// <summary>适配整个图像到可视窗口。</summary>
    void FitToView();

    /// <summary>恢复 1:1 原始像素比例。</summary>
    void ResetActualSize();

    /// <summary>读取当前选中或最近绘制的用户 ROI（不含算法锁定叠加标注）。</summary>
    VisionRoi? TryGetActiveUserRoi() => null;

    /// <summary>在图像上展示或高亮一个可编辑的用户 ROI。</summary>
    void ShowUserRoi(VisionRoi roi) { }

    /// <summary>清空所有用户绘制的 ROI（保留算法输出叠加标注）。</summary>
    void ClearUserRois() { }
}
