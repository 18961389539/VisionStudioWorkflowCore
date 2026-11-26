using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 一次直线拟合的统一几何定义。
    /// Chinese: 结果使用源图像像素中心坐标，整数坐标表示像素采样中心，允许亚像素值。
    /// <see cref="SegmentStart"/> 和
    /// <see cref="SegmentEnd"/> 只是拟合点覆盖范围内的有限支撑段；真正的拟合对象是由
    /// 单位法向量和偏移量定义的无限直线：Normal.X * X + Normal.Y * Y = Offset。
    /// English: Coordinates are in the source-image pixel coordinate system and may be subpixel.
    /// SegmentStart/SegmentEnd describe the finite support of the fitted samples; the fitted
    /// object itself is the infinite line defined by the unit normal and offset.
    /// </summary>
    public readonly record struct LineFitGeometry(
        PointD SegmentStart,
        PointD SegmentEnd,
        VectorD Normal,
        double Offset,
        double AngleDegrees)
    {
        private const double DegenerateLengthEpsilon = 1e-12;

        /// <summary>拟合点支撑段。</summary>
        public DetectedLineSegment Segment => new(SegmentStart, SegmentEnd);

        /// <summary>法向量是否为单位向量且直线数据均为有限值。</summary>
        public bool IsValid =>
            double.IsFinite(SegmentStart.X) &&
            double.IsFinite(SegmentStart.Y) &&
            double.IsFinite(SegmentEnd.X) &&
            double.IsFinite(SegmentEnd.Y) &&
            double.IsFinite(Normal.X) &&
            double.IsFinite(Normal.Y) &&
            double.IsFinite(Offset) &&
            double.IsFinite(AngleDegrees) &&
            Math.Abs(Normal.Length - 1.0) <= 1e-9 &&
            SegmentStart.DistanceTo(SegmentEnd) > DegenerateLengthEpsilon;

        /// <summary>
        /// 计算点到拟合无限直线的带符号距离。
        /// 正负号由稳定的法向量方向决定，不能用于推断边缘极性。
        /// </summary>
        public double SignedDistanceTo(PointD point)
        {
            if (!IsValid)
            {
                return double.NaN;
            }

            return Normal.X * point.X + Normal.Y * point.Y - Offset;
        }

        /// <summary>计算点到拟合无限直线的正距离。</summary>
        public double DistanceTo(PointD point)
        {
            return Math.Abs(SignedDistanceTo(point));
        }

        /// <summary>把点正交投影到拟合无限直线。</summary>
        public PointD Project(PointD point)
        {
            if (!IsValid)
            {
                return point;
            }

            double signedDistance = SignedDistanceTo(point);
            return new PointD(
                point.X - signedDistance * Normal.X,
                point.Y - signedDistance * Normal.Y);
        }

        /// <summary>计算一条参数直线与拟合无限直线的交点。</summary>
        public bool TryIntersect(PointD linePoint, VectorD lineDirection, out PointD intersection)
        {
            intersection = default;
            if (!IsValid || !double.IsFinite(linePoint.X) || !double.IsFinite(linePoint.Y))
            {
                return false;
            }

            double denominator = Normal.X * lineDirection.X + Normal.Y * lineDirection.Y;
            if (!double.IsFinite(denominator) || Math.Abs(denominator) <= DegenerateLengthEpsilon)
            {
                return false;
            }

            double parameter = (Offset - Normal.X * linePoint.X - Normal.Y * linePoint.Y) / denominator;
            if (!double.IsFinite(parameter))
            {
                return false;
            }

            intersection = new PointD(
                linePoint.X + lineDirection.X * parameter,
                linePoint.Y + lineDirection.Y * parameter);
            return double.IsFinite(intersection.X) && double.IsFinite(intersection.Y);
        }

        /// <summary>
        /// 从有限拟合段创建统一直线定义。
        /// 法向量方向固定为“X 分量优先为正；接近垂直时 Y 分量为正”，
        /// 使同一条无向直线不会因端点顺序变化而得到相反的偏移量。
        /// </summary>
        public static LineFitGeometry FromSegment(DetectedLineSegment segment)
        {
            VectorD tangent = segment.End - segment.Start;
            double length = tangent.Length;
            if (!double.IsFinite(length) || length <= DegenerateLengthEpsilon)
            {
                return default;
            }

            tangent = tangent.Normalized();
            VectorD normal = new(-tangent.Y, tangent.X);
            if (normal.X < -DegenerateLengthEpsilon ||
                Math.Abs(normal.X) <= DegenerateLengthEpsilon && normal.Y < 0)
            {
                normal = new VectorD(-normal.X, -normal.Y);
            }

            double offset = normal.X * segment.Start.X + normal.Y * segment.Start.Y;
            double angle = Math.Atan2(tangent.Y, tangent.X) * 180.0 / Math.PI;
            while (angle < -90.0)
            {
                angle += 180.0;
            }

            while (angle >= 90.0)
            {
                angle -= 180.0;
            }

            return new LineFitGeometry(segment.Start, segment.End, normal, offset, angle);
        }
    }
}
