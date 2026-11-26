namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 运行时开关的唯一状态源。
        /// Chinese: 分析/渲染/金字塔等开关集中在这里，命令层与装配层都直接读写该对象，
        /// 不再由控件再转发一层同名属性，也不再逐项透传 Get/Set 委托。
        /// English: The single option store for analysis/render/pyramid switches; both the command layer and the
        /// composition layer read and write this object directly.
        /// </summary>
        public ImageViewerRuntimeOptions RuntimeOptions { get; } = new();
    }
}