using System.Windows.Media;
using ImageViewer.Models;

namespace ImageViewer.Rendering
{
    /// <summary>
    /// <see cref="RoiColor"/> 与 WPF <see cref="Color"/> 之间的转换。
    /// Chinese: 这是模型层与 WPF 之间唯一的颜色转换边界，模型自身不引用 System.Windows.Media。
    /// English: The single color-conversion boundary between the model layer and WPF. The models
    /// themselves do not reference System.Windows.Media.
    /// </summary>
    public static class RoiColorWpfExtensions
    {
        public static Color ToColor(this RoiColor color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        public static RoiColor ToRoiColor(this Color color)
        {
            return RoiColor.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
