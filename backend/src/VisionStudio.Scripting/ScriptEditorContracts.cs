namespace VisionStudio.Scripting;

/// <summary>One editor diagnostic with 1-based line/column positions.</summary>
public sealed record ScriptDiagnostic(int Line, int Column, string Severity, string Code, string Message);

/// <summary>Validation result for script.csharp. Compilation never executes the script.</summary>
public sealed record ScriptValidationResult(IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public bool Valid => Diagnostics.All(diagnostic => !string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One completion item returned by the static, allowlisted script API index.</summary>
public sealed record ScriptCompletionItem(string Label, string Kind, string Detail, string InsertText);

/// <summary>
/// Formatting result for script.csharp. Syntax errors leave <see cref="Formatted"/> unchanged so the
/// editor never replaces the user's buffer with a partial rewrite.
/// </summary>
public sealed record ScriptFormatResult(string Formatted, IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public bool Valid => Diagnostics.All(diagnostic => !string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase));
}
