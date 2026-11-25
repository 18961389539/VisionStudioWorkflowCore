namespace VisionStudio.Engine;

// 诊断数据结构已下沉到 VisionStudio.Abstractions（CaliperDiagnosticContracts.cs）。
// 本文件只保留"捕获作用域"这一实现细节。

internal static class CaliperEdgeDiagnosticCapture
{
    private static readonly AsyncLocal<Action<CaliperEdgeDiagnostic>?> Current = new();
    public static bool Enabled => Current.Value is not null;
    public static void Publish(CaliperEdgeDiagnostic diagnostic) => Current.Value?.Invoke(diagnostic);

    public static IDisposable Begin(Action<CaliperEdgeDiagnostic> capture)
    {
        var previous = Current.Value;
        Current.Value = capture;
        return new Scope(previous);
    }

    private sealed class Scope(Action<CaliperEdgeDiagnostic>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
