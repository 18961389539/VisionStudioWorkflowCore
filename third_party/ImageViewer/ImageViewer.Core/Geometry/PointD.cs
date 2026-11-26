using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 与 UI 框架无关的二维坐标点。
    /// Chinese: 供 Core 算法、ROI 契约和持久化 DTO 共享，避免几何值类型依赖 WPF。
    /// English: Framework-neutral 2D point shared by Core algorithms, ROI contracts and persistence DTOs.
    /// </summary>
    public readonly record struct PointD(double X, double Y)
    {
        /// <summary>返回当前点到另一点的欧氏距离。</summary>
        public double DistanceTo(PointD other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>返回两个点之间的位移向量。</summary>
        public static VectorD operator -(PointD left, PointD right)
        {
            return new VectorD(left.X - right.X, left.Y - right.Y);
        }

        /// <summary>返回点沿向量平移后的坐标。</summary>
        public static PointD operator +(PointD point, VectorD vector)
        {
            return new PointD(point.X + vector.X, point.Y + vector.Y);
        }

        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y})";
    }
}
