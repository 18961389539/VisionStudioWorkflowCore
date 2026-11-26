using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Utils
{
    /// <summary>
    /// 模型坐标（PointD / VectorD）与 WPF 坐标（Point / Vector）之间的转换。
    /// Chinese: 这是模型层与 WPF 之间唯一的几何转换边界，模型自身不引用 System.Windows；
    /// 渲染、命中测试、几何工具等消费方跨层时显式调用这里的转换，与 RoiColor → Color 的适配同一模式。
    /// English: The single geometry-conversion boundary between the model layer and WPF. Models themselves do not
    /// reference System.Windows; consumers convert explicitly here, mirroring the RoiColor → Color adapter.
    /// </summary>
    public static class RoiGeometryWpfExtensions
    {
        public static Point ToWpfPoint(this PointD point)
        {
            return new Point(point.X, point.Y);
        }

        public static PointD ToPointD(this Point point)
        {
            return new PointD(point.X, point.Y);
        }

        public static Vector ToWpfVector(this VectorD vector)
        {
            return new Vector(vector.X, vector.Y);
        }

        public static VectorD ToVectorD(this Vector vector)
        {
            return new VectorD(vector.X, vector.Y);
        }

        public static Rect ToWpfRect(this RectD rect)
        {
            return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        }

        public static RectD ToRectD(this Rect rect)
        {
            return new RectD(rect.X, rect.Y, rect.Width, rect.Height);
        }

        /// <summary>批量转换为 WPF 点，供参数为 IEnumerable&lt;Point&gt; 的 WPF API 使用。</summary>
        public static IEnumerable<Point> ToWpfPoints(this IEnumerable<PointD> points)
        {
            return points.Select(point => point.ToWpfPoint());
        }

        /// <summary>批量转换为模型点。</summary>
        public static IEnumerable<PointD> ToPointDs(this IEnumerable<Point> points)
        {
            return points.Select(point => point.ToPointD());
        }

        /// <summary>批量转换为 WPF 点数组，供需要数组/索引访问的 WPF API 使用。</summary>
        public static Point[] ToWpfPointArray(this IEnumerable<PointD> points)
        {
            return points.Select(point => point.ToWpfPoint()).ToArray();
        }
    }
}