using System.Windows.Controls;
using VisionStudio.Wpf.Controls.ImageViewer;

namespace VisionStudio.Wpf.Views;

public partial class DeviceMonitorView : UserControl
{
    public DeviceMonitorView()
    {
        InitializeComponent();
    }

    public IVisionImageViewerAdapter CameraViewerAdapter => CameraViewerControl;
}
