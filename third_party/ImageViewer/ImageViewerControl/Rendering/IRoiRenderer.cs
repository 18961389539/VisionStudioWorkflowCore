using System.Windows.Media;
using ImageViewer.Models;

namespace ImageViewer.Rendering
{
    public interface IRoiRenderer
    {
        bool CanRender(RoiBase roi);

        void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected);
    }
}
