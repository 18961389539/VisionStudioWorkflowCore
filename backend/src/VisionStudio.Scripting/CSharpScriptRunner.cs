using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using OpenCvSharp;

namespace VisionStudio.Scripting;

/// <summary>
/// Built-in C# script runner. Compiles with a minimal reference set, rejects listed host/IO/reflection
/// APIs with a syntax-tree denylist (unlisted APIs are allowed), injects a cooperative loop budget,
/// and returns a typed result.
///
/// This is a trusted-code runtime: the syntax-tree gate is defense-in-depth, not a hard OS sandbox.
/// The host should restrict script authoring to engineers and treat imported workflows with scripts
/// as code that requires approval.
/// </summary>
internal static partial class CSharpScriptRunner
{
    private const int MaxCachedScripts = 64;
    private static readonly ConcurrentDictionary<string, Script<object>> Cache = new(StringComparer.Ordinal);
    private static readonly Regex ForbiddenDirective = new(@"^\s*#(r|load|line)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly ScriptOptions Options = BuildOptions();

    internal static async Task<ScriptExecutionResult> RunAsync(
        string source,
        ScriptExecutionOptions options,
        IReadOnlyDictionary<string, object?> inputs,
        Mat? image,
        CancellationToken cancellationToken)
    {
        // ScriptGlobals owns the image argument and disposes it on every failure path;
        // the node transfers ownership before calling the runner.
        using var globals = new ScriptGlobals(inputs, image);
        if (string.IsNullOrWhiteSpace(source))
            throw new ScriptCompilationException(["Script source is empty."]);
        if (Encoding.UTF8.GetByteCount(source) > 256 * 1024)
            throw new ScriptCompilationException(["Script source exceeds 256 KB."]);
        if (ForbiddenDirective.IsMatch(source))
            throw new ScriptCompilationException(["#r, #load and #line directives are not allowed in scripts."]);
        if (image is not null) ValidateImage(image, "input image", options.MaxImagePixels);

        var sourceHash = Hash(source);
        var compileWatch = Stopwatch.StartNew();
        var script = GetOrCompile(source, sourceHash, cancellationToken);
        compileWatch.Stop();

        var execution = new ScriptExecutionContext.ExecutionContext(options.TimeoutMs, options.MaxTicks, cancellationToken);
        using var scope = ScriptExecutionContext.Enter(execution);
        var executeWatch = Stopwatch.StartNew();
        object? returnValue = null;
        try
        {
            var state = await script.RunAsync(globals, cancellationToken: cancellationToken).ConfigureAwait(false);
            returnValue = state.ReturnValue;
        }
        catch (CompilationErrorException ex)
        {
            throw new ScriptCompilationException(FormatDiagnostics(ex.Diagnostics));
        }
        catch (ScriptUserException)
        {
            throw;
        }
        catch (ScriptTimeoutException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var line = execution.LastLine;
            var message = line is { } number
                ? $"C# script failed at line {number}: {ex.Message}"
                : $"C# script failed: {ex.Message}";
            throw new InvalidOperationException(message, ex);
        }
        executeWatch.Stop();
        execution.CheckFinal();

        var result = globals.Output.Result ?? returnValue;
        var imageResult = globals.Output.TakeImage();
        try
        {
            return BuildResult(
                NormalizeResult(result),
                imageResult,
                globals.Output.Number,
                globals.Output.Text,
                globals.Output.Boolean,
                globals.Output.Json,
                globals.Logs,
                execution.Ticks,
                compileWatch.Elapsed.TotalMilliseconds,
                executeWatch.Elapsed.TotalMilliseconds,
                sourceHash,
                options);
        }
        catch
        {
            imageResult?.Dispose();
            throw;
        }
    }

    internal static ScriptValidationResult ValidateSource(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ScriptValidationResult([new ScriptDiagnostic(1, 1, "Error", "SC000", "Script source is empty.")]);
        if (Encoding.UTF8.GetByteCount(source) > 256 * 1024)
            return new ScriptValidationResult([new ScriptDiagnostic(1, 1, "Error", "SC001", "Script source exceeds 256 KB.")]);
        if (ForbiddenDirective.IsMatch(source))
            return new ScriptValidationResult([new ScriptDiagnostic(1, 1, "Error", "SC002", "#r, #load and #line directives are not allowed in scripts.")]);

        var diagnostics = new List<ScriptDiagnostic>();
        var parseOptions = CSharpParseOptions.Default
            .WithKind(SourceCodeKind.Script)
            .WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var validator = new ForbiddenApiWalker();
        validator.Visit(tree.GetRoot(cancellationToken));
        foreach (var error in validator.Errors)
            diagnostics.Add(ParseForbiddenDiagnostic(error));

        try
        {
            var script = CSharpScript.Create<object>(source, Options, globalsType: typeof(ScriptGlobals));
            foreach (var diagnostic in script.Compile(cancellationToken))
                AddDiagnostic(diagnostics, diagnostic);
        }
        catch (CompilationErrorException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
                AddDiagnostic(diagnostics, diagnostic);
        }

        if (!diagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)) &&
            !source.Contains("Output.", StringComparison.Ordinal) &&
            !Regex.IsMatch(source, @"\breturn\b") &&
            !source.Contains("Fail(", StringComparison.Ordinal))
            diagnostics.Add(new ScriptDiagnostic(1, 1, "Warning", "SC003",
                "Script does not set Output, return a value, or call Fail; executing it will fail with E_OUTPUT."));

        return new ScriptValidationResult(diagnostics
            .GroupBy(item => (item.Line, item.Column, item.Code, item.Message))
            .Select(group => group.First())
            .OrderBy(item => item.Line).ThenBy(item => item.Column)
            .ToArray());
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


    private static ScriptExecutionResult BuildResult(
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
        string sourceHash,
        ScriptExecutionOptions options)
    {
        if (number is { } numeric && !double.IsFinite(numeric))
            throw new ScriptUserException("E_OUTPUT", "Output.Number must be finite.");
        if (text is not null && Encoding.UTF8.GetByteCount(text) > options.MaxOutputBytes)
            throw new ScriptUserException("E_OUTPUT_SIZE", $"Output.Text exceeds {options.MaxOutputBytes} bytes.");
        if (json is not null)
        {
            if (Encoding.UTF8.GetByteCount(json) > options.MaxOutputBytes)
                throw new ScriptUserException("E_OUTPUT_SIZE", $"Output.Json exceeds {options.MaxOutputBytes} bytes.");
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ScriptUserException("E_OUTPUT_JSON", "Output.Json is not valid JSON: " + ex.Message);
            }
        }
        if (image is not null) ValidateImage(image, "output image", options.MaxImagePixels);
        if (result is null && image is null && number is null && text is null && boolean is null && json is null)
            throw new ScriptUserException("E_OUTPUT", "Script did not set any output. Set Output.Result/Number/Text/Boolean/Json/Image, or return a primitive value.");

        return new ScriptExecutionResult(
            result, image, number, text, boolean, json, logs.ToArray(),
            ticks, compileMs, executeMs, sourceHash);
    }

    private static object? NormalizeResult(object? value) => value switch
    {
        null => null,
        double number => number,
        float number => (double)number,
        decimal number => (double)number,
        long integer => integer,
        int integer => (long)integer,
        short integer => (long)integer,
        byte integer => (long)integer,
        bool boolean => boolean,
        string text => text,
        _ => throw new ScriptUserException(
            "E_OUTPUT",
            $"Output.Result type '{value.GetType().FullName}' is not supported. Use Output.Json for structured data.")
    };

    private static void ValidateImage(Mat image, string role, int maxPixels)
    {
        if (image.Empty())
            throw new ScriptUserException("E_IMAGE", $"The {role} is empty.");
        if (image.Channels() is not (1 or 3 or 4))
            throw new ScriptUserException("E_IMAGE", $"The {role} must have 1, 3 or 4 channels; received {image.Channels()}.");
        if (image.Depth() is not (MatType.CV_8U or MatType.CV_16U or MatType.CV_32F))
            throw new ScriptUserException("E_IMAGE", $"The {role} depth must be 8U, 16U or 32F; received {image.Depth()}.");
        if ((long)image.Rows * image.Cols > maxPixels)
            throw new ScriptUserException("E_IMAGE", $"The {role} has {(long)image.Rows * image.Cols:N0} pixels; the limit is {maxPixels:N0}.");
    }

    private static Script<object> GetOrCompile(string source, string sourceHash, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(sourceHash, out var cached)) return cached;

        var parseOptions = CSharpParseOptions.Default
            .WithKind(SourceCodeKind.Script)
            .WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var syntaxErrors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (syntaxErrors.Length > 0)
            throw new ScriptCompilationException(FormatDiagnostics(syntaxErrors));

        var validator = new ForbiddenApiWalker();
        validator.Visit(tree.GetRoot(cancellationToken));
        if (validator.Errors.Count > 0)
            throw new ScriptCompilationException(validator.Errors);

        // 先插入原始行号标记，再插入循环预算 Tick；标记让运行时异常可以回指原始源码行，
        // 不依赖调试符号或被 Tick 改写后的行号。
        var tracked = new LineTrackingRewriter().Visit(tree.GetRoot(cancellationToken))!;
        var rewritten = new LoopGuardRewriter().Visit(tracked)!.ToFullString();
        var script = CSharpScript.Create<object>(rewritten, Options, globalsType: typeof(ScriptGlobals));
        var diagnostics = script.Compile(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (diagnostics.Length > 0)
            throw new ScriptCompilationException(FormatDiagnostics(diagnostics));

        if (Cache.Count >= MaxCachedScripts) Cache.Clear();
        Cache[sourceHash] = script;
        return script;
    }

    private static IReadOnlyList<string> FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var result = new List<string>();
        foreach (var diagnostic in diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Take(20))
        {
            var span = diagnostic.Location.GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            var column = span.StartLinePosition.Character + 1;
            result.Add($"{line}:{column} {diagnostic.Id}: {diagnostic.GetMessage()}");
        }
        return result.Count == 0 ? ["C# script failed to compile."] : result;
    }

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static ScriptOptions BuildOptions()
    {
        var references = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(List<>).Assembly,
            typeof(JsonSerializer).Assembly,
            typeof(Mat).Assembly,
            typeof(ScriptGlobals).Assembly
        };
        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports("System", "System.Collections.Generic", "System.Linq", "System.Text", "OpenCvSharp", "VisionStudio.Scripting")
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(false)
            .WithEmitDebugInformation(false);
    }

    private sealed class ForbiddenApiWalker : CSharpSyntaxWalker
    {
        private static readonly string[] ForbiddenNamespaces =
        [
            "System.IO", "System.Net", "System.Diagnostics", "System.Reflection",
            "System.Runtime.InteropServices", "System.Runtime.Loader", "System.Runtime.CompilerServices",
            "System.Security", "System.Threading", "System.Linq.Expressions", "System.Reflection.Emit",
            "Microsoft.Win32", "System.Management", "System.ServiceProcess", "System.CodeDom",
            "System.Resources", "System.Configuration", "System.DirectoryServices", "System.Windows",
            "System.Web", "System.Xml", "System.Data", "Microsoft.Data", "System.Drawing",
            "System.IO.Pipes", "System.IO.MemoryMappedFiles", "System.Net.Http", "System.Net.Sockets",
            "Microsoft.CodeAnalysis", "System.Runtime.Serialization"
        ];

        private static readonly HashSet<string> ForbiddenTypes = new(StringComparer.Ordinal)
        {
            "File", "Directory", "Path", "FileInfo", "DirectoryInfo", "FileStream", "Stream",
            "StreamReader", "StreamWriter", "MemoryMappedFile", "HttpClient", "HttpRequestMessage",
            "WebClient", "Socket", "TcpClient", "UdpClient", "Process", "ProcessStartInfo",
            "Assembly", "Activator", "AppDomain", "Environment", "Console", "Debug", "Trace",
            "EventLog", "Registry", "RegistryKey", "Marshal", "GCHandle", "GC", "Thread",
            "ThreadPool", "Task", "Timer", "CancellationTokenSource", "FileStorage", "VideoCapture",
            "VideoWriter", "DllImport", "DllImportAttribute", "UnmanagedCallersOnly",
            "UnmanagedCallersOnlyAttribute", "NativeLibrary", "Type"
        };

        private static readonly HashSet<string> ForbiddenMembers = new(StringComparer.Ordinal)
        {
            "GetType", "Invoke", "DynamicInvoke", "CreateDelegate", "Load", "LoadFrom",
            "GetAssemblies", "GetTypes", "GetMethod", "GetMethods", "GetProperty", "GetProperties",
            "GetField", "GetFields", "GetConstructor", "GetConstructors", "GetManifestResourceStream",
            "GetEnvironmentVariable", "SetEnvironmentVariable", "Exit", "FailFast", "Start",
            "WaitForExit", "Kill", "Open", "ReadAllText", "ReadAllBytes", "ReadAllLines",
            "WriteAllText", "WriteAllBytes", "WriteAllLines", "Copy", "Move", "Delete",
            "CreateDirectory", "GetFiles", "GetDirectories", "EnumerateFiles", "EnumerateDirectories",
            "Exists", "OpenRead", "OpenWrite", "OpenText", "AppendText", "AppendAllText"
        };

        private static readonly HashSet<string> ForbiddenCv2Members = new(StringComparer.Ordinal)
        {
            "ImRead", "ImWrite", "ImReadMulti", "ImWriteMulti", "ImReadWithMetadata", "ImWriteWithMetadata",
            "VideoCapture", "VideoWriter", "FileStorage", "ReadNet", "ReadNetFromONNX",
            "ReadNetFromTensorflow", "ReadNetFromCaffe", "ReadNetFromDarknet", "ReadNetFromTorch",
            "WaitKey", "ImShow", "NamedWindow", "DestroyAllWindows", "DestroyWindow",
            "MoveWindow", "ResizeWindow", "SetWindowProperty", "GetWindowProperty"
        };

        public List<string> Errors { get; } = [];

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            CheckName(node.Name?.ToString(), node);
            base.VisitUsingDirective(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.ValueText == "dynamic")
                Forbid(node, "dynamic is not allowed in scripts.");
            base.VisitIdentifierName(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var member = node.Name.Identifier.ValueText;
            if (ForbiddenMembers.Contains(member))
                Forbid(node, $"Member '{member}' is not allowed in scripts.");
            if (node.Expression is IdentifierNameSyntax root && ForbiddenTypes.Contains(root.Identifier.ValueText))
                Forbid(node, $"Type '{root.Identifier.ValueText}' is not allowed in scripts.");
            var full = node.ToString();
            CheckName(full, node);
            if (full.EndsWith(".Cv2." + member, StringComparison.Ordinal) || full.StartsWith("Cv2.", StringComparison.Ordinal))
            {
                if (ForbiddenCv2Members.Contains(member))
                    Forbid(node, $"Cv2.{member} performs file/device/UI I/O and is not allowed in scripts.");
            }
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            CheckType(node.Type, node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            CheckType(node.Type, node);
            base.VisitVariableDeclaration(node);
        }

        public override void VisitParameter(ParameterSyntax node)
        {
            if (node.Type is not null) CheckType(node.Type, node);
            base.VisitParameter(node);
        }

        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            CheckType(node.Type, node);
            base.VisitCastExpression(node);
        }

        public override void VisitSimpleBaseType(SimpleBaseTypeSyntax node)
        {
            CheckType(node.Type, node);
            base.VisitSimpleBaseType(node);
        }

        public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
        {
            Forbid(node, "typeof is not allowed in scripts.");
            base.VisitTypeOfExpression(node);
        }

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            if (node.Declaration is null)
                Forbid(node, "Catch-all clauses are not allowed; catching the script budget exception is unsafe.");
            else
            {
                var text = node.Declaration.Type.ToString();
                if (text is "Exception" or "System.Exception" or "TimeoutException" or "System.TimeoutException" or
                    "OperationCanceledException" or "System.OperationCanceledException" or
                    "ScriptTimeoutException" or "VisionStudio.Scripting.ScriptTimeoutException")
                    Forbid(node, $"Catching '{text}' would bypass script cancellation and timeout budgets.");
            }
            base.VisitCatchClause(node);
        }

        public override void VisitLockStatement(LockStatementSyntax node)
        {
            Forbid(node, "lock is not allowed in scripts; scripts must be single-threaded.");
            base.VisitLockStatement(node);
        }

        public override void VisitUnsafeStatement(UnsafeStatementSyntax node)
        {
            Forbid(node, "unsafe is not allowed in scripts.");
            base.VisitUnsafeStatement(node);
        }

        public override void VisitPointerType(PointerTypeSyntax node)
        {
            Forbid(node, "Pointer types are not allowed in scripts.");
            base.VisitPointerType(node);
        }

        public override void VisitFunctionPointerType(FunctionPointerTypeSyntax node)
        {
            Forbid(node, "Function pointer types are not allowed in scripts.");
            base.VisitFunctionPointerType(node);
        }

        public override void VisitStackAllocArrayCreationExpression(StackAllocArrayCreationExpressionSyntax node)
        {
            Forbid(node, "stackalloc is not allowed in scripts.");
            base.VisitStackAllocArrayCreationExpression(node);
        }

        public override void VisitFixedStatement(FixedStatementSyntax node)
        {
            Forbid(node, "fixed statements are not allowed in scripts.");
            base.VisitFixedStatement(node);
        }

        public override void VisitExternAliasDirective(ExternAliasDirectiveSyntax node)
        {
            Forbid(node, "extern aliases are not allowed in scripts.");
            base.VisitExternAliasDirective(node);
        }

        public override void VisitAwaitExpression(AwaitExpressionSyntax node)
        {
            Forbid(node, "await is not allowed in scripts; scripts execute synchronously.");
            base.VisitAwaitExpression(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.AsyncKeyword))
                Forbid(node, "async methods are not allowed in scripts.");
            base.VisitMethodDeclaration(node);
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.AsyncKeyword))
                Forbid(node, "async local functions are not allowed in scripts.");
            base.VisitLocalFunctionStatement(node);
        }

        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            if (node.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                Forbid(node, "async lambdas are not allowed in scripts.");
            base.VisitParenthesizedLambdaExpression(node);
        }

        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            if (node.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                Forbid(node, "async lambdas are not allowed in scripts.");
            base.VisitSimpleLambdaExpression(node);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            CheckName(node.Name.ToString(), node);
            base.VisitAttribute(node);
        }

        private void CheckType(TypeSyntax type, SyntaxNode node)
        {
            CheckName(type.ToString(), node);
            foreach (var token in type.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)))
                CheckName(token.ValueText, node);
        }

        private void CheckName(string? name, SyntaxNode node)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var normalized = name.StartsWith("global::", StringComparison.Ordinal) ? name[8..] : name;
            foreach (var ns in ForbiddenNamespaces)
            {
                if (normalized.Equals(ns, StringComparison.Ordinal) ||
                    normalized.StartsWith(ns + ".", StringComparison.Ordinal))
                {
                    Forbid(node, $"Namespace '{ns}' is not allowed in scripts.");
                    return;
                }
            }
            var last = normalized.Split('.').Last();
            if (ForbiddenTypes.Contains(normalized) || ForbiddenTypes.Contains(last))
                Forbid(node, $"Type '{normalized}' is not allowed in scripts.");
        }

        private void Forbid(SyntaxNode node, string message)
        {
            if (Errors.Count >= 20) return;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            Errors.Add($"{line}:1 {message}");
        }
    }

    /// <summary>
    /// Injects MarkLine(originalLine) before statements so runtime errors can report the original
    /// source line even after the loop guard rewrites the tree. Markers are separate statements and
    /// never change variable scope.
    /// </summary>
    private sealed class LineTrackingRewriter : CSharpSyntaxRewriter
    {
        private static StatementSyntax MarkLine(int line) =>
            SyntaxFactory.ParseStatement($"global::VisionStudio.Scripting.ScriptExecutionContext.MarkLine({line});");

        private static int LineOf(SyntaxNode node) =>
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static StatementSyntax MarkEmbedded(StatementSyntax visited, StatementSyntax original) =>
            visited is BlockSyntax ? visited : SyntaxFactory.Block(MarkLine(LineOf(original)), visited);

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            var statements = new List<StatementSyntax>(visited.Statements.Count * 2);
            for (var index = 0; index < visited.Statements.Count; index++)
            {
                if (index < node.Statements.Count)
                    statements.Add(MarkLine(LineOf(node.Statements[index])));
                statements.Add(visited.Statements[index]);
            }
            return visited.WithStatements(SyntaxFactory.List(statements));
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            var members = new List<MemberDeclarationSyntax>(visited.Members.Count * 2);
            for (var index = 0; index < visited.Members.Count; index++)
            {
                if (index < node.Members.Count && visited.Members[index] is GlobalStatementSyntax
                    && node.Members[index] is GlobalStatementSyntax original)
                    members.Add(SyntaxFactory.GlobalStatement(MarkLine(LineOf(original.Statement))));
                members.Add(visited.Members[index]);
            }
            return visited.WithMembers(SyntaxFactory.List(members));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            var statements = new List<StatementSyntax>(visited.Statements.Count * 2);
            for (var index = 0; index < visited.Statements.Count; index++)
            {
                if (index < node.Statements.Count)
                    statements.Add(MarkLine(LineOf(node.Statements[index])));
                statements.Add(visited.Statements[index]);
            }
            return visited.WithStatements(SyntaxFactory.List(statements));
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
            var elseClause = visited.Else is { } visitedElse && node.Else is { } originalElse
                ? visitedElse.WithStatement(MarkEmbedded(visitedElse.Statement, originalElse.Statement))
                : visited.Else;
            return visited.WithStatement(MarkEmbedded(visited.Statement, node.Statement)).WithElse(elseClause);
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(MarkEmbedded(visited.Statement, node.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(MarkEmbedded(visited.Statement, node.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(MarkEmbedded(visited.Statement, node.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(MarkEmbedded(visited.Statement, node.Statement));
        }
    }

    private sealed class LoopGuardRewriter : CSharpSyntaxRewriter
    {
        private static StatementSyntax Tick() =>
            SyntaxFactory.ParseStatement("global::VisionStudio.Scripting.ScriptExecutionContext.Tick();");

        private static BlockSyntax AddTick(BlockSyntax block) =>
            block.WithStatements(block.Statements.Insert(0, Tick()));

        private static StatementSyntax AddTick(StatementSyntax statement) =>
            statement is BlockSyntax block ? AddTick(block) : SyntaxFactory.Block(Tick(), statement);

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }
    }
}
