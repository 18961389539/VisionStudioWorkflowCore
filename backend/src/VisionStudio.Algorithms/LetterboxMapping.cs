namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Source-image ↔ model-input coordinate mapping shared by every ONNX image node.
/// Letterbox keeps the aspect ratio (uniform scale + centred padding); stretch scales
/// each axis independently. <see cref="ToSourceX"/>/<see cref="ToInputX"/> are exact
/// inverses; use the same instance for preprocessing and for mapping results back so
/// detection, pose, OBB and mask composition cannot drift apart.
/// </summary>
public readonly struct LetterboxMapping
{
    public LetterboxMapping(int sourceWidth, int sourceHeight, int inputWidth, int inputHeight, bool letterbox)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || inputWidth < 1 || inputHeight < 1)
            throw new InvalidOperationException("Letterbox mapping sizes must be positive.");
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        Letterbox = letterbox;
        if (letterbox)
        {
            var scale = Math.Min((double)inputWidth / sourceWidth, (double)inputHeight / sourceHeight);
            ScaleX = scale;
            ScaleY = scale;
            PadX = (inputWidth - sourceWidth * scale) / 2;
            PadY = (inputHeight - sourceHeight * scale) / 2;
        }
        else
        {
            ScaleX = (double)inputWidth / sourceWidth;
            ScaleY = (double)inputHeight / sourceHeight;
            PadX = 0;
            PadY = 0;
        }
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int InputWidth { get; }
    public int InputHeight { get; }
    public bool Letterbox { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }
    public double PadX { get; }
    public double PadY { get; }

    public double ToInputX(double sourceX) => sourceX * ScaleX + PadX;
    public double ToInputY(double sourceY) => sourceY * ScaleY + PadY;
    public double ToSourceX(double inputX) => (inputX - PadX) / ScaleX;
    public double ToSourceY(double inputY) => (inputY - PadY) / ScaleY;
}