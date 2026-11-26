using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 与 UI 框架无关的二维向量。
    /// Chinese: 供 Core 几何计算和 ROI 契约共享。
    /// English: Framework-neutral 2D vector shared by Core geometry and ROI contracts.
    /// </summary>
    public readonly record struct VectorD(double X, double Y)
    {
        /// <summary>返回向量长度的平方。</summary>
        public double LengthSquared => X * X + Y * Y;

        /// <summary>返回向量长度。</summary>
        public double Length => Math.Sqrt(LengthSquared);

        /// <summary>返回单位向量；零向量保持不变。</summary>
        public VectorD Normalized()
        {
            double length = Length;
            return length <= 1e-12 ? this : new VectorD(X / length, Y / length);
        }

        /// <summary>返回向量按标量缩放后的结果。</summary>
        public static VectorD operator *(VectorD vector, double scale)
        {
            return new VectorD(vector.X * scale, vector.Y * scale);
        }

        /// <summary>返回向量按标量缩放后的结果。</summary>
        public static VectorD operator *(double scale, VectorD vector)
        {
            return new VectorD(vector.X * scale, vector.Y * scale);
        }

        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y})";
    }
}
