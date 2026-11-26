
namespace ImageViewer.Models
{
    /// <summary>
    /// 参与直线拟合的有限点集支撑段。
    /// Chinese: 该段仅描述拟合点的覆盖范围，不等同于用于显示或测量的结果段；
    /// 需要无限拟合直线时使用 <see cref="LineFitGeometry"/>。
    /// English: Finite support segment of the points used by a line fit. It is distinct from
    /// the display/measurement segment and can be promoted to an infinite line definition.
    /// </summary>
    public readonly record struct DetectedLineSegment(PointD Start, PointD End);
}
