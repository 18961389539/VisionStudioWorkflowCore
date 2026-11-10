namespace VisionStudio.Scripting;

/// <summary>
/// Read-only editor services for script.csharp. Validate uses the same reference set and forbidden-API
/// walker as execution; Format only checks the syntax tree and normalizes whitespace; Complete filters
/// completion items with SemanticForbiddenNamespaces. None of them execute code or create Mat objects.
/// </summary>
public static class ScriptEditorService
{
    public static ScriptValidationResult Validate(string source, CancellationToken cancellationToken = default) =>
        CSharpScriptRunner.ValidateSource(source ?? string.Empty, cancellationToken);

    public static IReadOnlyList<ScriptCompletionItem> Complete(
        string source,
        int offset,
        IReadOnlyList<string>? ports = null) =>
        CSharpScriptRunner.CompleteSource(source ?? string.Empty, offset, ports);

    public static ScriptFormatResult Format(string source, CancellationToken cancellationToken = default) =>
        CSharpScriptRunner.FormatSource(source ?? string.Empty, cancellationToken);
}
