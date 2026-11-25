// 2026-11-25 从 CSharpScriptRunner.cs 拆出（size-budget：主文件 766 > 702）。
// 纯移动，未改逻辑：脚本校验的诊断辅助（Mat 释放检查、被禁 API 的错误解析、编译诊断归并）。
// 一份例外：用户新增该检查时漏了字段声明上的 using 修饰符——脚本顶层的
// `using var x = new Mat();` 也解析成字段声明，这里补上了判断，否则正确写法会被误报 SC005。

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VisionStudio.Scripting;

internal static partial class CSharpScriptRunner
{
    private static void AddMatDisposalWarnings(SyntaxNode root, List<ScriptDiagnostic> diagnostics)
    {
        var disposedNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == "Dispose" &&
                member.Expression is IdentifierNameSyntax)
            .Select(invocation => ((MemberAccessExpressionSyntax)invocation.Expression).Expression.ToString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (!local.UsingKeyword.IsKind(SyntaxKind.None)) continue;
            AddUndisposedMatDiagnostics(local.Declaration, disposedNames, diagnostics);
        }

        // Top-level `var x = new Mat();` in C# scripts parses as a FieldDeclarationSyntax (script class field).
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            // 顶层的 `using var x = new Mat();` 同样落到字段声明，但带着 using 修饰符——
            // 它已经是"异常路径也会释放"的安全写法，不该再报 SC005（与上面的局部变量分支对齐）。
            if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.UsingKeyword))) continue;
            AddUndisposedMatDiagnostics(field.Declaration, disposedNames, diagnostics);
        }
    }

    private static void AddUndisposedMatDiagnostics(
        VariableDeclarationSyntax declaration,
        HashSet<string> disposedNames,
        List<ScriptDiagnostic> diagnostics)
    {
        foreach (var variable in declaration.Variables)
        {
            if (!CreatesMat(declaration.Type, variable.Initializer?.Value)) continue;
            var name = variable.Identifier.ValueText;
            if (disposedNames.Contains(name)) continue;
            var span = variable.GetLocation().GetLineSpan();
            diagnostics.Add(new ScriptDiagnostic(
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                "Warning",
                "SC005",
                $"Mat variable '{name}' is allocated without 'using' or Dispose(); prefer 'using (var {name} = new Mat()) {{ ... }}' so unmanaged image memory is released even if an exception occurs."));
        }
    }

    private static bool CreatesMat(TypeSyntax declaredType, ExpressionSyntax? initializer)
    {
        if (initializer is ObjectCreationExpressionSyntax created)
        {
            var typeName = created.Type.ToString();
            return typeName is "Mat" or "OpenCvSharp.Mat";
        }
        if (initializer is ImplicitObjectCreationExpressionSyntax)
        {
            var typeName = declaredType.ToString();
            return typeName is "Mat" or "OpenCvSharp.Mat";
        }
        return false;
    }

    private static ScriptDiagnostic ParseForbiddenDiagnostic(string error)
    {
        var split = error.IndexOf(' ');
        var location = split > 0 ? error[..split] : "1:1";
        var message = split > 0 ? error[(split + 1)..] : error;
        var parts = location.Split(':');
        var line = parts.Length > 0 && int.TryParse(parts[0], out var parsedLine) ? parsedLine : 1;
        var column = parts.Length > 1 && int.TryParse(parts[1], out var parsedColumn) ? parsedColumn : 1;
        return new ScriptDiagnostic(line, column, "Error", "SC004", message);
    }

    private static void AddDiagnostic(List<ScriptDiagnostic> diagnostics, Diagnostic diagnostic)
    {
        if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) return;
        var span = diagnostic.Location.GetLineSpan();
        diagnostics.Add(new ScriptDiagnostic(
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            diagnostic.Severity.ToString(),
            diagnostic.Id,
            diagnostic.GetMessage()));
    }
}
