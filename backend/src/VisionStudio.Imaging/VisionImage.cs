using OpenCvSharp;

// 程序集归属：VisionStudio.Imaging（2026-11-25 从 VisionStudio.Engine 迁出）。
// 算法层只依赖本类型与 VisionRoi，不应反向依赖引擎运行时。

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

    /// <summary>
    /// 借用外部生命周期管理的像素缓冲（相机帧租约等）。
    /// 调用方把"谁负责释放"一起传进来，因此本层不需要知道租约类型——
    /// 这层是算法与相机实现之间的下沉点，不能反向依赖 CameraFrameLease。
    /// </summary>
    public static VisionImage FromLease(Mat image, IDisposable owner) => new(image, ownsMat: false, owner);
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
