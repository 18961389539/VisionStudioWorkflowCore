namespace VisionStudio.Engine;

// 诊断数据结构已下沉到 VisionStudio.Abstractions（CaliperDiagnosticContracts.cs）：
// 算法层与宿主 API 都需要这些 DTO，不应为了它们反向引用引擎。
// 本文件只保留"捕获作用域"这一实现细节。

/// <summary>
/// A scope belongs to one node dispatch, including on parallel branches. This keeps diagnostic
/// capture out of the plugin execution ABI and avoids retaining any profiles in production runs.
/// </summary>
internal static class CaliperCircleDiagnosticCapture
{
    private static readonly AsyncLocal<Action<CaliperCircleDiagnostic>?> Current = new();
    public static bool Enabled => Current.Value is not null;
    public static void Publish(CaliperCircleDiagnostic diagnostic) => Current.Value?.Invoke(diagnostic);

    public static IDisposable Begin(Action<CaliperCircleDiagnostic> capture)
    {
        var previous = Current.Value;
        Current.Value = capture;
        return new Scope(previous);
    }

    private sealed class Scope(Action<CaliperCircleDiagnostic>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
