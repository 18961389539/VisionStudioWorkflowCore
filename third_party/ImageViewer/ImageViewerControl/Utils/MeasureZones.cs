using System;
using ImageViewer.Models;

namespace ImageViewer.Utils
{
    /// <summary>
    /// 形位测量量的"公差带/评估量"定义集中处。
    ///
    /// Chinese:
    /// 平行度、垂直度、同心度这几个名字在 GB/T 1182（等同 ISO 1101）里都是**带基准的定向/定位公差带**：
    /// 平行度是"被测要素相对基准、限制在两平行平面（或圆柱面）之间的区域"，同心度是"被测轴线相对基准
    /// 轴线、限制在直径 t 的圆柱面内的区域"。本控件只做**二维图像测量**，没有基准要素、没有第三维，
    /// 因此**无法**实现完整形位公差。
    ///
    /// 这里把每个量明确定义为"一种可评估的几何指标"，并给出**与其物理含义一致的评估量**：
    ///   - 角度类（平行度/垂直度）：评估量 = 角度偏差（度）。方向公差带宽度随长度变化，
    ///     在不知道被测长度与基准长度时无法折算成单一宽度值，所以角度偏差本身就是最诚实的评估量。
    ///   - 同心度：评估量 = **同心度公差带直径** = 2 × 圆心距。这是 ISO 1101 里与公差带可比的量
    ///     （公差带是直径 t 的圆，所以 t = 2e），而不是单个偏心量 e。
    ///
    /// English: Parallelism / perpendicularity / concentricity are datum-based tolerance zones in ISO 1101.
    /// A 2D image-only tool has no datum feature and no third dimension, so full GD&amp;T is not implementable.
    /// This type fixes, in one place, what each quantity's *evaluated value* means so the UI, tolerance
    /// evaluation and reports cannot drift apart.
    /// </summary>
    public static class MeasureZones
    {
        /// <summary>
        /// 同心度公差带直径（像素）= 2 × 两圆心距。
        /// Chinese: ISO 1101 的同心度公差带是"直径 t 的圆"，因此可评估量取 t = 2e 才能与公差带直接比较；
        /// 直接拿偏心量 e 当测得值与公差比是常见错误（会宽松一倍）。
        /// </summary>
        public static double ConcentricityZoneDiameter(ConcentricityMeasureRoi roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return roi.CenterDistance * 2.0;
        }

        /// <summary>
        /// 该 ROI 的主测量值单位是否为"度"（角度量，不随 PixelSize 缩放）。
        /// Chinese: 供信息面板/导出/属性面板统一判断格式，避免各处各自枚举类型。
        /// </summary>
        public static bool IsAngularMeasuredValue(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return roi is ParallelismMeasureRoi
                or PerpendicularityMeasureRoi
                or AngleMeasureRoi;
        }

        /// <summary>
        /// 该 ROI 是否为"形位公差风格"的量（其名称对应 ISO 1101 公差带，但本工具只用二维几何指标近似）。
        /// Chinese: 供界面提示"该判定为二维近似，非完整形位公差"，避免操作员把结果当成合规性判定。
        /// </summary>
        public static bool IsGeometricToleranceStyle(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return roi is ParallelismMeasureRoi
                or PerpendicularityMeasureRoi
                or ConcentricityMeasureRoi;
        }
    }
}
