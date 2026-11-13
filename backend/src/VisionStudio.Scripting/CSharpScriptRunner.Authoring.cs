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

internal static partial class CSharpScriptRunner
{

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
}
