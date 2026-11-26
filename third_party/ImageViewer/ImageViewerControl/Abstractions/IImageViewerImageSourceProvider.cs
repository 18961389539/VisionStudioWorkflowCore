using System.Windows.Media;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerImageSourceProvider
    {
        ImageSource? ViewerImage { get; }
    }
}