namespace VisionStudio.Engine.Camera;

/// <summary>Owned pixels plus an explicit processing domain; distinct graph type prevents domain loss.</summary>
public sealed class VisionDomainImage : IVisionDomainImage, IDisposable
{
    private VisionImage? _image;
    public VisionImage Image => _image ?? throw new ObjectDisposedException(nameof(VisionDomainImage));
    public VisionRegion Domain { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;
    public object NativeImage => Image.NativeImage;
    public VisionDomainImage(VisionImage image, VisionRegion domain)
    {
        if (image.Width != domain.Width || image.Height != domain.Height) throw new ArgumentException("Domain/image dimensions differ.");
        _image = image; Domain = domain;
    }
    public void Dispose() => Interlocked.Exchange(ref _image, null)?.Dispose();
}
