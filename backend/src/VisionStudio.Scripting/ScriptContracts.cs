using System.Diagnostics;
using OpenCvSharp;

namespace VisionStudio.Scripting;

/// <summary>Read-only point projection exposed to scripts.</summary>
public readonly record struct ScriptPoint(double X, double Y);

/// <summary>
/// Typed input access for scripts. Only primitives, number lists and point lists are exposed;
/// images are exposed separately through <see cref="ScriptGlobals.Image"/>.
/// </summary>
public sealed class ScriptInputs
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    internal ScriptInputs(IReadOnlyDictionary<string, object?> values) => _values = values;

    public bool Has(string name) => _values.ContainsKey(name);

    public object? Value(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw Error("E_INPUT", $"Input '{name}' is not connected.");

    public double Number(string name) => Value(name) switch
    {
        double value => value,
        long value => value,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not a number.")
    };

    public long Integer(string name) => Value(name) switch
    {
        long value => value,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not an integer.")
    };

    public bool Boolean(string name) => Value(name) switch
    {
        bool value => value,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not a boolean.")
    };

    public string Text(string name) => Value(name) switch
    {
        string value => value,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not a string.")
    };

    public IReadOnlyList<double> Numbers(string name) => Value(name) switch
    {
        double[] values => values,
        IReadOnlyList<double> values => values,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not a number list.")
    };

    public IReadOnlyList<ScriptPoint> Points(string name) => Value(name) switch
    {
        ScriptPoint[] values => values,
        IReadOnlyList<ScriptPoint> values => values,
        _ => throw Error("E_INPUT_TYPE", $"Input '{name}' is not a point list.")
    };

    private static ScriptUserException Error(string code, string message) =>
        new(code, message, ScriptExecutionContext.CurrentLine);
}

/// <summary>
/// Writable script result. Image assignment clones immediately so a script can safely use
/// <c>using</c> for its working Mats and still publish the result.
/// </summary>
public sealed class ScriptOutput : IDisposable
{
    private Mat? _image;

    public object? Result { get; set; }
    public double? Number { get; set; }
    public string? Text { get; set; }
    public bool? Boolean { get; set; }
    public string? Json { get; set; }

    public Mat? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            var clone = value?.Clone();
            _image?.Dispose();
            _image = clone;
        }
    }

    internal Mat? TakeImage()
    {
        var image = _image;
        _image = null;
        return image;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}

/// <summary>
/// Globals available to every C# script. The script assembly only references
/// VisionStudio.Scripting, OpenCvSharp and a small BCL surface; it cannot see host services.
/// </summary>
public sealed class ScriptGlobals : IDisposable
{
    private const int MaxLogEntries = 100;
    private const int MaxLogLength = 2000;
    private readonly List<string> _logs = [];
    private Mat? _image;

    internal ScriptGlobals(IReadOnlyDictionary<string, object?> inputs, Mat? image)
    {
        Input = new ScriptInputs(inputs);
        Output = new ScriptOutput();
        _image = image;
    }

    public ScriptInputs Input { get; }
    public ScriptOutput Output { get; }

    /// <summary>Clone of the connected image input, owned by the script execution.</summary>
    public Mat? Image => _image;

    public void Log(string message) => Log("info", message);

    public void Log(string level, string message)
    {
        if (_logs.Count >= MaxLogEntries) return;
        var text = $"{level}: {message ?? string.Empty}";
        _logs.Add(text.Length <= MaxLogLength ? text : text[..MaxLogLength] + "…");
    }

    public void Fail(string code, string message) =>
        throw new ScriptUserException(
            string.IsNullOrWhiteSpace(code) ? "E_SCRIPT" : code,
            message ?? "Script failure.",
            ScriptExecutionContext.CurrentLine);

    internal IReadOnlyList<string> Logs => _logs;

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
        Output.Dispose();
    }
}

public sealed class ScriptUserException : Exception
{
    public ScriptUserException(string code, string message, int? line = null)
        : base(line is { } number ? $"{message} (line {number})" : message)
    {
        Code = code;
        Line = line;
    }

    public string Code { get; }

    /// <summary>Original script line captured by the injected line marker, when available.</summary>
    public int? Line { get; }
}

public sealed class ScriptTimeoutException : Exception
{
    public ScriptTimeoutException(string message) : base(message)
    {
    }
}

public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(IReadOnlyList<string> diagnostics)
        : base("C# script failed to compile: " + string.Join(" | ", diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Execution budget enforced cooperatively by the loop guard injected into scripts.</summary>
public sealed class ScriptExecutionContext
{
    private static readonly AsyncLocal<ExecutionContext?> Current = new();

    internal static IDisposable Enter(ExecutionContext context)
    {
        var previous = Current.Value;
        Current.Value = context;
        return new Scope(previous);
    }

    /// <summary>
    /// Called by the injected loop guard and at function entry. A script that catches
    /// <see cref="Exception"/>/<see cref="ScriptTimeoutException"/> is rejected by the validator.
    /// </summary>
    public static void Tick() => Current.Value?.Tick();

    /// <summary>Called by the injected line markers before each statement execution.</summary>
    public static void MarkLine(int line) => Current.Value?.MarkLine(line);

    internal static int? CurrentLine => Current.Value?.LastLine;

    internal sealed class ExecutionContext
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _timeoutMs;
        private readonly long _maxTicks;
        private readonly CancellationToken _cancellationToken;
        private long _ticks;
        private int? _line;

        internal ExecutionContext(int timeoutMs, long maxTicks, CancellationToken cancellationToken)
        {
            _timeoutMs = timeoutMs;
            _maxTicks = maxTicks;
            _cancellationToken = cancellationToken;
        }

        internal long Ticks => Interlocked.Read(ref _ticks);

        internal int? LastLine => _line;

        internal void MarkLine(int line) => _line = line;

        internal void Tick()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var ticks = Interlocked.Increment(ref _ticks);
            var suffix = _line is { } line ? $" (line {line})" : string.Empty;
            if (ticks > _maxTicks)
                throw new ScriptTimeoutException($"C# script exceeded the tick budget ({_maxTicks:N0}){suffix}.");
            if (_clock.ElapsedMilliseconds > _timeoutMs)
                throw new ScriptTimeoutException($"C# script exceeded the timeout ({_timeoutMs} ms){suffix}.");
        }

        internal void CheckFinal()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_clock.ElapsedMilliseconds > _timeoutMs)
            {
                var suffix = _line is { } line ? $" (line {line})" : string.Empty;
                throw new ScriptTimeoutException($"C# script exceeded the timeout ({_timeoutMs} ms){suffix}.");
            }
        }
    }

    private sealed class Scope(ExecutionContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public sealed record ScriptExecutionOptions(
    int TimeoutMs,
    long MaxTicks,
    int MaxOutputBytes,
    int MaxImagePixels);

public sealed class ScriptExecutionResult : IDisposable
{
    private Mat? _image;

    internal ScriptExecutionResult(
        object? result,
        Mat? image,
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
    public Mat? Image => _image;
    public double? Number { get; }
    public string? Text { get; }
    public bool? Boolean { get; }
    public string? Json { get; }
    public IReadOnlyList<string> Logs { get; }
    public long Ticks { get; }
    public double CompileMs { get; }
    public double ExecuteMs { get; }
    public string SourceHash { get; }

    internal Mat? TakeImage()
    {
        var image = _image;
        _image = null;
        return image;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}
