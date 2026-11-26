using System.Windows;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 一次边缘吸附搜索的结果。
    /// Chinese: <see cref="Found"/> 为 false 时其余字段无意义；用结构体避免每次鼠标移动都分配对象。
    /// English: One edge-snap search result. The remaining fields are meaningless when
    /// <see cref="Found"/> is false; a struct keeps pointer-move feedback allocation free.
    /// </summary>
    public readonly record struct EdgeSnapResult(bool Found, Point Point, double Score, double Confidence)
    {
        /// <summary>未找到可信边缘。</summary>
        public static EdgeSnapResult NotFound => default;
    }
}