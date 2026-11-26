using System.Windows.Controls;
using VisionStudio.Wpf.Controls.ImageViewer;

namespace VisionStudio.Wpf.Views;

public partial class ProductionRunView : UserControl
{
    public ProductionRunView()
    {
        InitializeComponent();
    }

    public IVisionImageViewerAdapter PreviewViewerAdapter => ProductionPreviewViewer;
}
