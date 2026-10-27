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
/// Built-in C# script runner. Compiles with a minimal reference set, rejects host/IO/reflection
/// APIs with a syntax-tree allowlist, injects a cooperative loop budget, and returns a typed result.
///
/// This is a trusted-code runtime: the syntax-tree gate is defense-in-depth, not a hard OS sandbox.
/// The host should restrict script authoring to engineers and treat imported workflows with scripts
/// as code that requires approval.
/// </summary>
internal static class CSharpScriptRunner
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

    internal static ScriptFormatResult FormatSource(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ScriptFormatResult(source, [new ScriptDiagnostic(1, 1, "Error", "SC000", "Script source is empty.")]);
        if (Encoding.UTF8.GetByteCount(source) > 256 * 1024)
            return new ScriptFormatResult(source, [new ScriptDiagnostic(1, 1, "Error", "SC001", "Script source exceeds 256 KB.")]);
        if (ForbiddenDirective.IsMatch(source))
            return new ScriptFormatResult(source, [new ScriptDiagnostic(1, 1, "Error", "SC002", "#r, #load and #line directives are not allowed in scripts.")]);

        var parseOptions = CSharpParseOptions.Default
            .WithKind(SourceCodeKind.Script)
            .WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var diagnostics = new List<ScriptDiagnostic>();
        foreach (var diagnostic in tree.GetDiagnostics(cancellationToken))
            AddDiagnostic(diagnostics, diagnostic);
        if (diagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            return new ScriptFormatResult(source, diagnostics);

        // NormalizeWhitespace is the public Roslyn normalizer; it keeps tokens, comments and
        // directives intact while replacing arbitrary whitespace with one statement per line.
        var formatted = tree.GetRoot(cancellationToken).NormalizeWhitespace(indentation: "    ", eol: "\n").ToFullString();
        if (!formatted.EndsWith('\n')) formatted += "\n";
        return new ScriptFormatResult(formatted, diagnostics);
    }

    internal static IReadOnlyList<ScriptCompletionItem> CompleteSource(string source, int offset, IReadOnlyList<string>? ports)
    {
        offset = Math.Clamp(offset, 0, source.Length);
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var prefix = source[lineStart..offset];
        var knownPorts = (ports is { Count: > 0 } ? ports : ["a", "b", "c", "d", "e", "f", "g", "h"])
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var portMatch = Regex.Match(prefix, @"Input\.(Number|Integer|Boolean|Text|Numbers|Points)\(\s*""([^""]*)$");
        if (portMatch.Success)
        {
            var partial = portMatch.Groups[2].Value;
            return knownPorts
                .Where(port => port.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Select(port => new ScriptCompletionItem(port, "Port", "script input", port))
                .ToArray();
        }

        var inputMatch = Regex.Match(prefix, @"Input\.([A-Za-z]*)$");
        if (inputMatch.Success)
            return InputMethods.Where(item => item.Label.StartsWith(inputMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();

        var outputMatch = Regex.Match(prefix, @"Output\.([A-Za-z]*)$");
        if (outputMatch.Success)
            return OutputProperties.Where(item => item.Label.StartsWith(outputMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();

        var cv2Match = Regex.Match(prefix, @"Cv2\.([A-Za-z]*)$");
        if (cv2Match.Success)
            return Cv2Methods.Where(item => item.Label.StartsWith(cv2Match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();

        var imageMatch = Regex.Match(prefix, @"Image\.([A-Za-z]*)$");
        if (imageMatch.Success)
            return ImageMembers.Where(item => item.Label.StartsWith(imageMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();

        var mathMatch = Regex.Match(prefix, @"Math\.([A-Za-z]*)$");
        if (mathMatch.Success)
            return MathMethods.Where(item => item.Label.StartsWith(mathMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToArray();

        // OpenCvSharp 常量枚举：ColorConversionCodes.、ThresholdTypes.、MatType. 等。
        // 使用负向后行断言，避免把 MyColorConversionCodes. 当成 ColorConversionCodes.。
        var constantMatch = Regex.Match(prefix, @"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z0-9_]*)$");
        if (constantMatch.Success)
        {
            if (MemberContexts.TryGetValue(constantMatch.Groups[1].Value, out var members))
                return Filter(members, constantMatch.Groups[2].Value);
            // 常见脚本写法：var gray = new Mat(); gray.… —— 不做完整语义模型，只识别
            // 能直接判定为 Mat 的本地变量，避免把通用补全塞进 Mat 成员上下文。
            if (IsMatVariable(source, constantMatch.Groups[1].Value))
                return Filter(ImageMembers, constantMatch.Groups[2].Value);
            // 其它本地变量（Scalar、Rect、Point、string、数组…）走一次轻量语义绑定；
            // 失败时退回通用补全，不影响原有行为。
            var semantic = SemanticCompleteSource(source, offset, constantMatch.Groups[1].Value, constantMatch.Groups[2].Value);
            if (semantic is not null) return semantic;
        }

        var tokenMatch = Regex.Match(prefix, @"([A-Za-z_][A-Za-z0-9_]*)$");
        var token = tokenMatch.Success ? tokenMatch.Groups[1].Value : string.Empty;
        return GeneralCompletions
            .Where(item => token.Length == 0 || item.Label.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Kind == "Snippet" ? 1 : 0)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static readonly ScriptCompletionItem[] InputMethods =
    [
        new("Number", "Method", "double Number(string name)", "Number(\"\")"),
        new("Integer", "Method", "long Integer(string name)", "Integer(\"\")"),
        new("Boolean", "Method", "bool Boolean(string name)", "Boolean(\"\")"),
        new("Text", "Method", "string Text(string name)", "Text(\"\")"),
        new("Numbers", "Method", "IReadOnlyList<double> Numbers(string name)", "Numbers(\"\")"),
        new("Points", "Method", "IReadOnlyList<ScriptPoint> Points(string name)", "Points(\"\")"),
        new("Has", "Method", "bool Has(string name)", "Has(\"\")"),
        new("Value", "Method", "object? Value(string name)", "Value(\"\")")
    ];

    private static readonly ScriptCompletionItem[] OutputProperties =
    [
        new("Result", "Property", "object? (double/long/bool/string)", "Result = "),
        new("Number", "Property", "double?", "Number = "),
        new("Text", "Property", "string?", "Text = "),
        new("Boolean", "Property", "bool?", "Boolean = "),
        new("Json", "Property", "string? valid JSON", "Json = "),
        new("Image", "Property", "Mat? (cloned on assignment)", "Image = ")
    ];

    private static readonly ScriptCompletionItem[] Cv2Methods =
    new[]
    {
        "CvtColor", "Mean", "MeanStdDev", "Threshold", "AdaptiveThreshold", "GaussianBlur", "MedianBlur",
        "BilateralFilter", "MorphologyEx", "GetStructuringElement", "FindContours", "BoundingRect", "ContourArea",
        "ArcLength", "MinAreaRect", "ApproxPolyDP", "ConvexHull", "FitLine", "FitEllipse", "MinEnclosingCircle",
        "PointPolygonTest", "ConnectedComponents", "ConnectedComponentsWithStats", "CountNonZero", "FindNonZero",
        "Kmeans", "HoughCircles", "HoughLinesP", "Canny", "Sobel", "Scharr", "Laplacian", "Filter2D",
        "MatchTemplate", "MinMaxLoc", "Resize", "WarpAffine", "WarpPerspective", "GetRotationMatrix2D",
        "GetAffineTransform", "GetPerspectiveTransform", "PerspectiveTransform", "FindHomography", "Undistort",
        "SolvePnP", "Rodrigues", "DrawContours", "FillPoly", "Polylines", "Rectangle", "Circle", "Line", "PutText",
        "ImDecode", "ImEncode", "Split", "Merge", "Normalize", "Compare", "Absdiff", "Add", "Subtract", "Multiply",
        "Divide", "ScaleAdd", "ConvertScaleAbs", "EqualizeHist", "Flip", "Rotate", "Transpose", "CopyMakeBorder",
        "PyrDown", "PyrUp", "BitwiseAnd", "BitwiseOr", "BitwiseXor", "BitwiseNot", "InRange", "GoodFeaturesToTrack",
        "CornerHarris"
    }.Select(name => new ScriptCompletionItem(name, "Method", "OpenCV (allowlisted)", name + "(")).ToArray();

    /// <summary>
    /// 常量/枚举成员补全：这些类型在脚本里几乎只以 "Type.Member" 形式出现，
    /// 静态索引比引入完整语义模型更轻，也不会把 ImRead 之类的禁用 API 带进候选。
    /// </summary>
    private static readonly Dictionary<string, ScriptCompletionItem[]> MemberContexts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ColorConversionCodes"] = Constants("color conversion code",
                "BGR2GRAY", "GRAY2BGR", "BGR2RGB", "RGB2BGR", "BGRA2BGR", "BGR2BGRA", "BGRA2GRAY", "GRAY2BGRA",
                "BGR2HSV", "HSV2BGR", "BGR2HLS", "HLS2BGR", "BGR2Lab", "Lab2BGR", "BGR2XYZ", "XYZ2BGR",
                "BGR2YUV", "YUV2BGR", "BayerBG2BGR", "BayerGB2BGR", "BayerRG2BGR", "BayerGR2BGR"),
            ["ThresholdTypes"] = Constants("threshold type",
                "Binary", "BinaryInv", "Trunc", "Tozero", "TozeroInv", "Mask", "Otsu", "Triangle"),
            ["AdaptiveThresholdTypes"] = Constants("adaptive threshold method", "MeanC", "GaussianC"),
            ["MorphTypes"] = Constants("morphology operation",
                "Erode", "Dilate", "Open", "Close", "Gradient", "TopHat", "BlackHat", "HitMiss"),
            ["MorphShapes"] = Constants("structuring element shape", "Rect", "Cross", "Ellipse"),
            ["NormTypes"] = Constants("norm type",
                "INF", "L1", "L2", "L2SQR", "Hamming", "Hamming2", "Relative", "MinMax"),
            ["RetrievalModes"] = Constants("contour retrieval mode", "External", "List", "CComp", "Tree", "FloodFill"),
            ["ContourApproximationModes"] = Constants("contour approximation mode",
                "ApproxNone", "ApproxSimple", "ApproxTC89L1", "ApproxTC89KCOS"),
            ["TemplateMatchModes"] = Constants("template matching mode",
                "SqDiff", "SqDiffNormed", "CCorr", "CCorrNormed", "CCoeff", "CCoeffNormed"),
            ["CmpTypes"] = Constants("comparison type", "EQ", "GT", "GE", "LT", "LE", "NE"),
            ["MatType"] = Constants("matrix element type",
                "CV_8UC1", "CV_8UC2", "CV_8UC3", "CV_8UC4", "CV_16UC1", "CV_16UC3", "CV_16SC1", "CV_16SC3",
                "CV_32SC1", "CV_32SC3", "CV_32FC1", "CV_32FC2", "CV_32FC3", "CV_32FC4", "CV_64FC1", "CV_64FC3", "CV_64FC4"),
            ["KMeansFlags"] = Constants("k-means initialization", "RandomCenters", "PpCenters", "UseInitialLabels"),
            ["InterpolationFlags"] = Constants("interpolation method", "Nearest", "Linear", "Cubic", "Area", "Lanczos4"),
            ["BorderTypes"] = Constants("border mode", "Constant", "Replicate", "Reflect", "Reflect101", "Wrap", "Isolated"),
            ["LineTypes"] = Constants("line drawing mode", "Link4", "Link8", "AntiAlias"),
            ["HersheyFonts"] = Constants("font face",
                "HersheySimplex", "HersheyPlain", "HersheyDuplex", "HersheyComplex", "HersheyTriplex",
                "HersheyComplexSmall", "HersheyScriptSimplex", "HersheyScriptComplex"),
            ["ImreadModes"] = Constants("image decode mode", "Unchanged", "Grayscale", "Color", "AnyDepth", "AnyColor"),
            ["Scalar"] = new ScriptCompletionItem[]
            {
                new("All", "Method", "Scalar All(double value)", "All("),
                new("FromRgb", "Method", "Scalar FromRgb(int r, int g, int b)", "FromRgb("),
                new("RandomColor", "Method", "Scalar RandomColor()", "RandomColor()")
            }.Concat(Constants("named color",
                "Red", "Green", "Blue", "White", "Black", "Yellow", "Cyan", "Magenta", "Orange", "Gray", "LimeGreen"))
                .ToArray()
        };

    private static ScriptCompletionItem[] Constants(string detail, params string[] names) =>
        names.Select(name => new ScriptCompletionItem(name, "Constant", detail, name)).ToArray();

    private static ScriptCompletionItem[] Filter(ScriptCompletionItem[] items, string prefix) =>
        prefix.Length == 0
            ? items
            : items.Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Heuristic Mat detection for completion only (execution still relies on the real compiler):
    /// accepts Mat declarations and the common factories that always return a Mat.
    /// </summary>
    private static bool IsMatVariable(string source, string name)
    {
        var escaped = Regex.Escape(name);
        return Regex.IsMatch(source,
            $@"(?:\b(?:OpenCvSharp\.)?Mat\??\s+{escaped}\b|\bvar\s+{escaped}\s*=\s*(?:new\s+(?:OpenCvSharp\.)?Mat\b|Image\b|[A-Za-z_][A-Za-z0-9_]*\s*\.\s*Clone\s*\(|Cv2\s*\.\s*(?:GetStructuringElement|ImDecode|GetRotationMatrix2D|GetAffineTransform|GetPerspectiveTransform|FindHomography|GetGaussianKernel|GetOptimalNewCameraMatrix)\s*\())",
            RegexOptions.CultureInvariant);
    }

    private static readonly string[] SemanticForbiddenNamespaces =
    [
        "System.IO", "System.Net", "System.Reflection", "System.Diagnostics", "System.Threading",
        "System.Runtime.InteropServices", "System.Security", "Microsoft.Win32", "System.Management",
        "System.Drawing", "Microsoft.CodeAnalysis"
    ];

    /// <summary>
    /// Binds the receiver local with the real script compilation. This is only a completion fallback:
    /// compile/runtime behavior still comes from <see cref="RunAsync"/>, and failures return null so
    /// the caller can keep the static suggestions.
    /// </summary>
    private static IReadOnlyList<ScriptCompletionItem>? SemanticCompleteSource(
        string source, int offset, string receiver, string memberPrefix)
    {
        if (source.Length > 64 * 1024) return null;
        try
        {
            var script = CSharpScript.Create<object>(source, Options, globalsType: typeof(ScriptGlobals));
            var compilation = script.GetCompilation();
            var tree = compilation.SyntaxTrees
                .FirstOrDefault(candidate => candidate.GetText().ToString() == source);
            if (tree is null) return null;
            var model = compilation.GetSemanticModel(tree);
            var identifier = tree.GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(node => node.Identifier.ValueText == receiver && node.SpanStart < offset)
                .OrderByDescending(node => node.SpanStart)
                .FirstOrDefault();
            if (identifier is null) return null;
            var info = model.GetSymbolInfo(identifier);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var type = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IFieldSymbol field when !field.IsStatic => field.Type,
                IPropertySymbol property when !property.IsStatic => property.Type,
                _ => null
            };
            if (type is IArrayTypeSymbol array)
                return IsSemanticForbidden(array.ElementType) ? null : ArrayMemberCompletions(array, memberPrefix);
            if (type is not INamedTypeSymbol named || IsSemanticForbidden(named)) return null;
            return NamedTypeMemberCompletions(named, memberPrefix);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSemanticForbidden(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return SemanticForbiddenNamespaces.Any(forbidden =>
            ns.Equals(forbidden, StringComparison.Ordinal) ||
            ns.StartsWith(forbidden + ".", StringComparison.Ordinal));
    }

    private static IReadOnlyList<ScriptCompletionItem> NamedTypeMemberCompletions(INamedTypeSymbol type, string prefix)
    {
        var items = new List<ScriptCompletionItem>();
        foreach (var group in type.GetMembers()
            .Where(member => member.DeclaredAccessibility == Accessibility.Public)
            .Where(member => !member.IsStatic && !member.IsImplicitlyDeclared)
            .Where(member => member.ContainingType.SpecialType != SpecialType.System_Object)
            .Where(member => member.Name is not "GetType" and not "MemberwiseClone")
            .GroupBy(member => member.Name, StringComparer.Ordinal))
        {
            var representative = group.OrderBy(member => member is IMethodSymbol method ? method.Parameters.Length : 0).First();
            switch (representative)
            {
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    var signature = $"{DisplayType(method.ReturnType)} {method.Name}(" +
                        string.Join(", ", method.Parameters.Select(parameter => $"{DisplayType(parameter.Type)} {parameter.Name}")) + ")";
                    var insert = method.TypeParameters.Length > 0 ? method.Name + "<"
                        : method.Parameters.Length == 0 ? method.Name + "()" : method.Name + "(";
                    items.Add(new ScriptCompletionItem(method.Name, "Method",
                        group.Count() > 1 ? $"{signature} (+{group.Count() - 1} 重载)" : signature, insert));
                    break;
                case IPropertySymbol property when !property.IsIndexer:
                    items.Add(new ScriptCompletionItem(property.Name, "Property", DisplayType(property.Type), property.Name));
                    break;
                case IFieldSymbol field:
                    items.Add(new ScriptCompletionItem(field.Name, "Field", DisplayType(field.Type), field.Name));
                    break;
            }
        }
        return items
            .Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Kind == "Method" ? 1 : 0)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
    }

    private static IReadOnlyList<ScriptCompletionItem> ArrayMemberCompletions(IArrayTypeSymbol array, string prefix)
    {
        var arrayType = DisplayType(array);
        var items = new ScriptCompletionItem[]
        {
            new("Length", "Property", "int", "Length"),
            new("LongLength", "Property", "long", "LongLength"),
            new("Rank", "Property", "int", "Rank"),
            new("Clone", "Method", $"{arrayType} Clone()", "Clone()"),
            new("GetLength", "Method", "int GetLength(int dimension)", "GetLength("),
            new("GetValue", "Method", "object? GetValue(int index)", "GetValue("),
            new("SetValue", "Method", "void SetValue(object? value, int index)", "SetValue(")
        };
        return items.Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string DisplayType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static readonly ScriptCompletionItem[] ImageMembers =
    [
        new("Clone", "Method", "Mat Clone()", "Clone()"),
        new("Rows", "Property", "int", "Rows"),
        new("Cols", "Property", "int", "Cols"),
        new("Width", "Property", "int", "Width"),
        new("Height", "Property", "int", "Height"),
        new("Channels", "Method", "int Channels()", "Channels()"),
        new("Depth", "Method", "int Depth()", "Depth()"),
        new("Empty", "Method", "bool Empty()", "Empty()"),
        new("SetTo", "Method", "Mat SetTo(Scalar)", "SetTo("),
        new("CopyTo", "Method", "void CopyTo(Mat)", "CopyTo("),
        new("At", "Method", "T At<T>(int row, int col)", "At<"),
        new("Get", "Method", "T Get<T>(int row, int col)", "Get<"),
        new("Dispose", "Method", "void Dispose()", "Dispose()")
    ];

    private static readonly ScriptCompletionItem[] MathMethods =
    new[]
    {
        "Abs", "Min", "Max", "Round", "Floor", "Ceiling", "Sqrt", "Pow", "Atan2", "Sin", "Cos", "PI"
    }.Select(name => new ScriptCompletionItem(name, "Method", "System.Math", name)).ToArray();

    private static readonly ScriptCompletionItem[] GeneralCompletions =
    [
        new("Input", "Variable", "typed script inputs", "Input"),
        new("Output", "Variable", "typed script outputs", "Output"),
        new("Image", "Variable", "input Mat or null", "Image"),
        new("Log", "Method", "void Log(string message)", "Log("),
        new("Fail", "Method", "void Fail(string code, string message)", "Fail("),
        new("Mat", "Type", "OpenCvSharp.Mat", "Mat"),
        new("Scalar", "Type", "OpenCvSharp.Scalar", "Scalar"),
        new("Point", "Type", "OpenCvSharp.Point", "Point"),
        new("Point2f", "Type", "OpenCvSharp.Point2f", "Point2f"),
        new("Rect", "Type", "OpenCvSharp.Rect", "Rect"),
        new("Size", "Type", "OpenCvSharp.Size", "Size"),
        new("Vec3b", "Type", "OpenCvSharp.Vec3b", "Vec3b"),
        new("Cv2", "Type", "OpenCvSharp.Cv2", "Cv2"),
        new("MatType", "Type", "OpenCvSharp.MatType", "MatType"),
        new("ColorConversionCodes", "Type", "OpenCvSharp.ColorConversionCodes", "ColorConversionCodes"),
        new("ThresholdTypes", "Type", "OpenCvSharp.ThresholdTypes", "ThresholdTypes"),
        new("AdaptiveThresholdTypes", "Type", "OpenCvSharp.AdaptiveThresholdTypes", "AdaptiveThresholdTypes"),
        new("MorphTypes", "Type", "OpenCvSharp.MorphTypes", "MorphTypes"),
        new("MorphShapes", "Type", "OpenCvSharp.MorphShapes", "MorphShapes"),
        new("NormTypes", "Type", "OpenCvSharp.NormTypes", "NormTypes"),
        new("RetrievalModes", "Type", "OpenCvSharp.RetrievalModes", "RetrievalModes"),
        new("ContourApproximationModes", "Type", "OpenCvSharp.ContourApproximationModes", "ContourApproximationModes"),
        new("TemplateMatchModes", "Type", "OpenCvSharp.TemplateMatchModes", "TemplateMatchModes"),
        new("CmpTypes", "Type", "OpenCvSharp.CmpTypes", "CmpTypes"),
        new("KMeansFlags", "Type", "OpenCvSharp.KMeansFlags", "KMeansFlags"),
        new("InterpolationFlags", "Type", "OpenCvSharp.InterpolationFlags", "InterpolationFlags"),
        new("BorderTypes", "Type", "OpenCvSharp.BorderTypes", "BorderTypes"),
        new("LineTypes", "Type", "OpenCvSharp.LineTypes", "LineTypes"),
        new("HersheyFonts", "Type", "OpenCvSharp.HersheyFonts", "HersheyFonts"),
        new("ImreadModes", "Type", "OpenCvSharp.ImreadModes", "ImreadModes"),
        new("if", "Keyword", "conditional", "if () {\n    \n}"),
        new("for", "Keyword", "loop", "for (var i = 0; i < ; i++) {\n    \n}"),
        new("foreach", "Keyword", "loop", "foreach (var item in ) {\n    \n}"),
        new("while", "Keyword", "loop", "while () {\n    \n}"),
        new("return", "Keyword", "return a value", "return "),
        new("var", "Keyword", "inferred local", "var "),
        new("new", "Keyword", "create an object", "new "),
        new("true", "Keyword", "boolean", "true"),
        new("false", "Keyword", "boolean", "false"),
        new("null", "Keyword", "null", "null"),
        new("Output.Number", "Snippet", "write a numeric output", "Output.Number = 0;"),
        new("Output.Image", "Snippet", "convert input to gray and publish", "var gray = new Mat();\nCv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);\nOutput.Image = gray;\ngray.Dispose();"),
        new("try", "Snippet", "guarded OpenCV call", "try {\n    \n} catch (OpenCvSharpException ex) {\n    Fail(\"E_CV\", ex.Message);\n}")
    ];

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
