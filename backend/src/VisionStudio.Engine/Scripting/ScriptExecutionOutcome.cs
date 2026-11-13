using OpenCvSharp;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Scripting;

/// <summary>
/// Backend-neutral result of one script execution. The image is owned by this outcome until
/// <see cref="TakeImage"/> transfers it (or <see cref="Dispose"/> releases it).
/// </summary>
public sealed class ScriptExecutionOutcome : IDisposable
{
    private VisionImage? _image;

    public ScriptExecutionOutcome(
        object? result,
        VisionImage? image,
        double? number,
        string? text,
        bool? boolean,
        string? json,
        IReadOnlyList<string> logs,
        long ticks,
        double compileMs,
        double executeMs,
        string sourceHash)
    {
        Result = result;
        _image = image;
        Number = number;
        Text = text;
        Boolean = boolean;
        Json = json;
        Logs = logs;
        Ticks = ticks;
        CompileMs = compileMs;
        ExecuteMs = executeMs;
        SourceHash = sourceHash;
    }

    public object? Result { get; }
    public VisionImage? Image => _image;
    public double? Number { get; }
    public string? Text { get; }
    public bool? Boolean { get; }
    public string? Json { get; }
    public IReadOnlyList<string> Logs { get; }
    public long Ticks { get; }
    public double CompileMs { get; }
    public double ExecuteMs { get; }
    public string SourceHash { get; }

    /// <summary>Transfers image ownership to the caller.</summary>
    public VisionImage? TakeImage()
    {
        var image = _image;
        _image = null;
        return image;
    }

    public void Dispose() => Interlocked.Exchange(ref _image, null)?.Dispose();
}
