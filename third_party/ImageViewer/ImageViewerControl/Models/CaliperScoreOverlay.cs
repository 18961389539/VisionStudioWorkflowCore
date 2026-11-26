
namespace ImageViewer.Models
{
    public enum CaliperOverlayStatus
    {
        Valid,
        Rejected,
        Invalid
    }

    public readonly record struct CaliperScoreOverlay(PointD Position, string Text, CaliperOverlayStatus Status);
}
