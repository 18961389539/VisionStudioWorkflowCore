using System.Reflection;
using OpenCvSharp;
using VisionStudio.Scripting;

namespace VisionStudio.Engine.Tests;

/// <summary>Script editor validation/completion: original-source lines and allowlisted API index.</summary>
public sealed class ScriptEditorServiceTests
{
    [Fact]
    public void Validate_AcceptsOutputScript()
    {
        var result = ScriptEditorService.Validate("Output.Number = Input.Number(\"a\") * 2;");
        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == "Error");
    }

    [Fact]
    public void Validate_ReportsSyntaxAndForbiddenApiLines()
    {
        var syntax = ScriptEditorService.Validate("Output.Number = 1;\nvar broken = ;");
        Assert.False(syntax.Valid);
        Assert.Contains(syntax.Diagnostics, item => item.Line == 2 && item.Severity == "Error");

        var forbidden = ScriptEditorService.Validate("var text = System.IO.File.ReadAllText(\"x\");");
        Assert.False(forbidden.Valid);
        Assert.Contains(forbidden.Diagnostics, item => item.Code == "SC004" && item.Message.Contains("System.IO"));
    }

    [Fact]
    public void Validate_WarnsWhenScriptHasNoOutput()
    {
        var result = ScriptEditorService.Validate("var a = Input.Number(\"a\");");
        Assert.True(result.Valid);
        Assert.Contains(result.Diagnostics, item => item.Code == "SC003" && item.Severity == "Warning");
    }

    [Fact]
    public void Format_NormalizesIndentationAndKeepsTheScriptValid()
    {
        var result = ScriptEditorService.Format("if (true) {\nOutput.Number = 1;\nvar gray = new Mat();\n}\n");
        Assert.True(result.Valid);
        Assert.Contains("    Output.Number = 1;", result.Formatted);
        Assert.Contains("    var gray = new Mat();", result.Formatted);
        Assert.True(ScriptEditorService.Validate(result.Formatted).Valid);
    }

    [Fact]
    public void Format_KeepsCommentsAndRefusesSyntaxErrors()
    {
        var commented = ScriptEditorService.Format("// keep me\nOutput.Number = 2;\n");
        Assert.Contains("// keep me", commented.Formatted);

        const string broken = "var broken = ;";
        var syntax = ScriptEditorService.Format(broken);
        Assert.False(syntax.Valid);
        Assert.Equal(broken, syntax.Formatted);
        Assert.Contains(syntax.Diagnostics, item => item.Line == 1 && item.Severity == "Error");
    }

    [Fact]
    public void Complete_ReturnsAllowlistedMembersAndPorts()
    {
        var input = ScriptEditorService.Complete("Input.", 6);
        Assert.Contains(input, item => item.Label == "Number");
        Assert.Contains(input, item => item.Label == "Text");

        var output = ScriptEditorService.Complete("Output.", 7);
        Assert.Contains(output, item => item.Label == "Number");
        Assert.Contains(output, item => item.Label == "Image");

        var cv2 = ScriptEditorService.Complete("Cv2.", 4);
        Assert.Contains(cv2, item => item.Label == "CvtColor");
        Assert.DoesNotContain(cv2, item => item.Label == "ImRead");

        var ports = ScriptEditorService.Complete("Input.Number(\"", 14, ["a", "b"]);
        Assert.Equal(["a", "b"], ports.Select(item => item.Label).ToArray());
    }

    [Fact]
    public void Validate_AcceptsTheContourSummarySnippet()
    {
        const string source = """
            var gray = new Mat();
            Cv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);
            var binary = new Mat();
            Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Otsu);
            Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var areas = contours.Select(item => Cv2.ContourArea(item)).OrderByDescending(area => area).ToArray();
            Output.Json = "{\"count\":" + contours.Length + ",\"largest\":" + (areas.Length > 0 ? areas[0] : 0) + "}";
            gray.Dispose();
            binary.Dispose();
            """;
        var result = ScriptEditorService.Validate(source);
        Assert.True(result.Valid, string.Join(" | ", result.Diagnostics.Select(item => $"{item.Line}:{item.Column} {item.Code} {item.Message}")));
    }

    [Fact]
    public void Complete_ReturnsOpenCvConstantMembersAndCommonCv2Methods()
    {
        const string colorPrefix = "ColorConversionCodes.";
        var colors = ScriptEditorService.Complete(colorPrefix, colorPrefix.Length);
        Assert.Contains(colors, item => item.Label == "BGR2GRAY");
        Assert.All(colors, item => Assert.Equal("Constant", item.Kind));

        const string filteredPrefix = "Cv2.CvtColor(Image, gray, ColorConversionCodes.BGR2G";
        var filtered = ScriptEditorService.Complete(filteredPrefix, filteredPrefix.Length);
        Assert.Contains(filtered, item => item.Label == "BGR2GRAY");
        Assert.All(filtered, item => Assert.StartsWith("BGR2G", item.Label));

        const string matTypePrefix = "MatType.";
        Assert.Contains(ScriptEditorService.Complete(matTypePrefix, matTypePrefix.Length), item => item.Label == "CV_8UC1");

        const string scalarPrefix = "Scalar.";
        var scalar = ScriptEditorService.Complete(scalarPrefix, scalarPrefix.Length);
        Assert.Contains(scalar, item => item.Label == "All" && item.Kind == "Method");
        Assert.Contains(scalar, item => item.Label == "Red" && item.Kind == "Constant");

        const string cv2Prefix = "Cv2.";
        var cv2 = ScriptEditorService.Complete(cv2Prefix, cv2Prefix.Length);
        Assert.Contains(cv2, item => item.Label == "Kmeans");
        Assert.Contains(cv2, item => item.Label == "ApproxPolyDP");
        Assert.DoesNotContain(cv2, item => item.Label == "ImRead");
    }

    [Fact]
    public void Complete_ReturnsMatMembersForLocalMatVariables()
    {
        const string local = "var gray = new Mat();\ngray.";
        var members = ScriptEditorService.Complete(local, local.Length);
        Assert.Contains(members, item => item.Label == "Clone");
        Assert.Contains(members, item => item.Label == "Channels");
        Assert.DoesNotContain(members, item => item.Label == "Input");

        const string filteredSource = "Mat mask = new Mat();\nmask.Ch";
        var filtered = ScriptEditorService.Complete(filteredSource, filteredSource.Length);
        Assert.Contains(filtered, item => item.Label == "Channels");
        Assert.All(filtered, item => Assert.StartsWith("Ch", item.Label));

        const string cloned = "var copy = gray.Clone();\ncopy.";
        Assert.Contains(ScriptEditorService.Complete(cloned, cloned.Length), item => item.Label == "Clone");

        const string matrix = "var m = Cv2.GetAffineTransform(src, dst);\nm.";
        Assert.Contains(ScriptEditorService.Complete(matrix, matrix.Length), item => item.Label == "Rows");

        const string notMat = "var value = 1;\nvalue.";
        Assert.DoesNotContain(ScriptEditorService.Complete(notMat, notMat.Length), item => item.Label == "Clone");
    }

    [Fact]
    public void Complete_BindsOtherLocalTypesSemantically()
    {
        const string scalarSource = "var mean = Scalar.All(0);\nmean.";
        var scalar = ScriptEditorService.Complete(scalarSource, scalarSource.Length);
        Assert.Contains(scalar, item => item.Label == "Val0");
        Assert.Contains(scalar, item => item.Label == "ToVec3b");
        Assert.DoesNotContain(scalar, item => item.Label == "GetType");

        const string rectSource = "var rect = new Rect(1, 2, 3, 4);\nrect.";
        var rect = ScriptEditorService.Complete(rectSource, rectSource.Length);
        Assert.Contains(rect, item => item.Label == "Width");
        Assert.Contains(rect, item => item.Label == "Height");

        const string textSource = "var text = \"abc\";\ntext.";
        var text = ScriptEditorService.Complete(textSource, textSource.Length);
        Assert.Contains(text, item => item.Label == "Substring");
        Assert.Contains(text, item => item.Label == "Length");

        const string arraySource = "var contours = new Point[2][];\ncontours.";
        var array = ScriptEditorService.Complete(arraySource, arraySource.Length);
        Assert.Contains(array, item => item.Label == "Length");

        // 语义补全仍遵守执行器的禁用命名空间边界，不能把 FileStream 成员带进候选。
        const string forbiddenSource = "var file = System.IO.File.OpenRead(\"x\");\nfile.";
        Assert.DoesNotContain(ScriptEditorService.Complete(forbiddenSource, forbiddenSource.Length),
            item => item.Label == "ReadByte");
    }

    [Fact]
    public void Cv2Completions_AllExistOnTheCv2Type()
    {
        const string prefix = "Cv2.";
        var items = ScriptEditorService.Complete(prefix, prefix.Length);
        var methods = typeof(Cv2).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
            Assert.True(methods.Contains(item.Label), $"Cv2.{item.Label} is not a public static method.");
    }

    [Fact]
    public void ConstantCompletions_AllExistOnTheirOpenCvSharpTypes()
    {
        var contexts = new Dictionary<string, Type>
        {
            ["ColorConversionCodes"] = typeof(ColorConversionCodes),
            ["ThresholdTypes"] = typeof(ThresholdTypes),
            ["AdaptiveThresholdTypes"] = typeof(AdaptiveThresholdTypes),
            ["MorphTypes"] = typeof(MorphTypes),
            ["MorphShapes"] = typeof(MorphShapes),
            ["NormTypes"] = typeof(NormTypes),
            ["RetrievalModes"] = typeof(RetrievalModes),
            ["ContourApproximationModes"] = typeof(ContourApproximationModes),
            ["TemplateMatchModes"] = typeof(TemplateMatchModes),
            ["CmpTypes"] = typeof(CmpTypes),
            ["MatType"] = typeof(MatType),
            ["KMeansFlags"] = typeof(KMeansFlags),
            ["InterpolationFlags"] = typeof(InterpolationFlags),
            ["BorderTypes"] = typeof(BorderTypes),
            ["LineTypes"] = typeof(LineTypes),
            ["HersheyFonts"] = typeof(HersheyFonts),
            ["ImreadModes"] = typeof(ImreadModes),
            ["Scalar"] = typeof(Scalar)
        };
        foreach (var (name, type) in contexts)
        {
            var prefix = name + ".";
            var items = ScriptEditorService.Complete(prefix, prefix.Length);
            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                var member = type.GetMember(item.Label, BindingFlags.Public | BindingFlags.Static).FirstOrDefault();
                Assert.True(member is not null, $"{name}.{item.Label} is not a public static member of {type.FullName}.");
            }
        }
    }
}
