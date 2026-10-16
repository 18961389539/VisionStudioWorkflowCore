using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Workflow-owned image wrapper. It can own a Mat directly or hold a lease to a shared camera frame.
/// </summary>
public sealed class VisionImage : IVisionImage, IDisposable
{
    private IDisposable? _owner;
    private readonly bool _ownsMat;
    private Mat? _mat;
    internal string CoordinateFrame { get; private set; }

    private VisionImage(Mat mat, bool ownsMat, IDisposable? owner, string coordinateFrame = "CurrentImage")
    {
        _mat = mat;
        _ownsMat = ownsMat;
        _owner = owner;
        CoordinateFrame = coordinateFrame;
    }

    public Mat Mat => _mat ?? throw new ObjectDisposedException(nameof(VisionImage));
    public int Width => Mat.Width;
    public int Height => Mat.Height;
    public object NativeImage => Mat;

    public static VisionImage Own(Mat mat) => new(mat, ownsMat: true, owner: null);
    public static VisionImage Borrow(Mat mat) => new(mat, ownsMat: false, owner: null);
    public static VisionImage FromLease(CameraFrameLease lease) => new(lease.Image, ownsMat: false, owner: lease);
    public VisionImage CloneOwned() => new(Mat.Clone(), ownsMat: true, owner: null, CoordinateFrame);

    internal void SetCoordinateFrame(string coordinateFrame)
    {
        if (string.IsNullOrWhiteSpace(coordinateFrame))
            throw new ArgumentException("Image coordinate frame cannot be empty.", nameof(coordinateFrame));
        CoordinateFrame = coordinateFrame;
    }

    public void Dispose()
    {
        var mat = Interlocked.Exchange(ref _mat, null);
        if (_ownsMat) mat?.Dispose();
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
