using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Scripting;

namespace VisionStudio.Engine.Scripting;

/// <summary>
/// Execution backend for <c>script.csharp</c>. In-process is the historical trusted-code path;
/// WorkerProcess moves Roslyn execution into a killable child process without changing node semantics.
/// </summary>
public interface IScriptExecutionBackend
{
    ValueTask<ScriptExecutionOutcome> RunAsync(
        string source,
        ScriptExecutionOptions options,
        IReadOnlyDictionary<string, object?> inputs,
        Mat? image,
        CancellationToken cancellationToken);
}

/// <summary>Historical in-process runner. Kept as the default and as the worker's inner backend.</summary>
public sealed class InProcessScriptExecutionBackend : IScriptExecutionBackend
{
    public async ValueTask<ScriptExecutionOutcome> RunAsync(
        string source,
        ScriptExecutionOptions options,
        IReadOnlyDictionary<string, object?> inputs,
        Mat? image,
        CancellationToken cancellationToken)
    {
        using var result = await CSharpScriptRunner.RunAsync(source, options, inputs, image, cancellationToken)
            .ConfigureAwait(false);
        var outputImage = result.TakeImage();
        return new ScriptExecutionOutcome(
            result.Result,
            outputImage is null ? null : VisionImage.Own(outputImage),
            result.Number,
            result.Text,
            result.Boolean,
            result.Json,
            result.Logs,
            result.Ticks,
            result.CompileMs,
            result.ExecuteMs,
            result.SourceHash);
    }
}
