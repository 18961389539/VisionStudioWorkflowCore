using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Scripting;

namespace VisionStudio.Engine.Tests;

/// <summary>Built-in C# script node: outputs, image flow, budget and forbidden-API contracts.</summary>
public sealed class CSharpScriptNodeTests
{
    [Fact]
    public async Task Script_ComputesNumberFromTypedInput()
    {
        var result = await Run("""
            Output.Number = Input.Number("a") * 2;
            Log("computed");
            """, new() { ["a"] = VisionValue.Double(3) });
        Assert.Equal(6d, result.Outputs["number"].Require<double>("number"));
        Assert.Contains("computed", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task Script_TopLevelReturnBecomesResult()
    {
        var result = await Run("""
            return Input.Number("a") + 1;
            """, new() { ["a"] = VisionValue.Double(3) });
        Assert.Equal(4d, result.Outputs["result"].Require<double>("result"));
    }

    [Fact]
    public async Task Script_ProcessesImageAndPublishesMatPlusScalar()
    {
        using var source = new Mat(8, 12, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var inputs = new Dictionary<string, VisionValue> { ["image"] = Image(source) };
        var result = await Run("""
            if (Image is null) Fail("E_IMAGE", "image input is required");
            var gray = new Mat();
            Cv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);
            Output.Image = gray;
            Output.Number = Cv2.Mean(gray).Val0;
            Output.Text = "gray";
            gray.Dispose();
            """, inputs, extra: """{"maxImagePixels":1000,"timeoutMs":2000}""");
        using var outputImage = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(1, outputImage.Mat.Channels());
        Assert.Equal(source.Width, outputImage.Width);
        Assert.Equal(source.Height, outputImage.Height);
        Assert.Equal("gray", result.Outputs["text"].Require<string>("text"));
        Assert.True(result.Outputs["number"].Require<double>("number") > 0);
    }

    [Fact]
    public async Task Script_RejectsFileReflectionAndDirectives()
    {
        var file = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""var text = System.IO.File.ReadAllText("secret.txt");"""));
        Assert.Contains("System.IO", file.Message);

        var reflection = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""var type = typeof(object);"""));
        Assert.Contains("typeof", reflection.Message);

        var directive = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""#r "System.IO.FileSystem.dll"\nOutput.Text = "x";"""));
        Assert.Contains("#r", directive.Message);

        var catchAll = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""try { while (true) { } } catch { }"""));
        Assert.Contains("Catch-all", catchAll.Message);

        var task = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""var task = Task.Run(() => 1);"""));
        Assert.Contains("Task", task.Message);

        var gc = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            Run("""GC.Collect();"""));
        Assert.Contains("GC", gc.Message);
    }

    [Fact]
    public async Task Script_StopsInfiniteLoopWithBudget()
    {
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ScriptTimeoutException>(() =>
            Run("""
                while (true) { }
                """, extra: """{"timeoutMs":50,"maxTicks":100}"""));
        stopwatch.Stop();
        Assert.Contains("tick budget", error.Message);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"timeout guard took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Script_RejectsUnsupportedInputAndMissingOutputs()
    {
        using var source = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(1));
        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run("Output.Number = 1;", new() { ["a"] = Image(source) }));
        Assert.Contains("supported script input types", unsupported.Message);

        var missing = await Assert.ThrowsAsync<ScriptUserException>(() =>
            Run("var a = Input.Number(\"a\");", new() { ["a"] = VisionValue.Double(1) }));
        Assert.Contains("did not set any output", missing.Message);
    }

    [Fact]
    public async Task Script_RuntimeErrorReportsOriginalScriptLine()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""
            var values = new int[1];
            Output.Number = values[2];
            """));
        Assert.Contains("line 2", error.Message);

        var loopError = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""
            var values = new int[1];
            for (var i = 0; i < 1; i++)
            {
                Output.Number = values[i + 1];
            }
            """));
        Assert.Contains("line 4", loopError.Message);

        var functionError = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""
            void Boom()
            {
                var values = new int[1];
                Output.Number = values[2];
            }
            Boom();
            """));
        Assert.Contains("line 4", functionError.Message);
    }

    [Fact]
    public async Task Script_UserFailureReportsOriginalScriptLine()
    {
        var failure = await Assert.ThrowsAsync<ScriptUserException>(() => Run("""
            var value = 1;
            Fail("E_TEST", "boom");
            """));
        Assert.Contains("line 2", failure.Message);
        Assert.Equal(2, failure.Line);

        var typeError = await Assert.ThrowsAsync<ScriptUserException>(() => Run("""
            Output.Number = Input.Text("a").Length;
            """, new() { ["a"] = VisionValue.Double(1) }));
        Assert.Contains("line 1", typeError.Message);
        Assert.Equal(1, typeError.Line);
    }

    [Fact]
    public async Task Script_RejectsMalformedJsonAndOversizedOutputImage()
    {
        var json = await Assert.ThrowsAsync<ScriptUserException>(() =>
            Run("""Output.Json = "not-json";"""));
        Assert.Contains("not valid JSON", json.Message);

        var image = await Assert.ThrowsAsync<ScriptUserException>(() =>
            Run("""
                Output.Image = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(0));
                """, extra: """{"maxImagePixels":1000}"""));
        Assert.Contains("pixels", image.Message);
    }

    private static async Task<NodeExecutorResult> Run(
        string source,
        Dictionary<string, VisionValue>? inputs = null,
        string? extra = null)
    {
        inputs ??= [];
        var parameters = new Dictionary<string, JsonElement>
        {
            ["source"] = JsonSerializer.SerializeToElement(source)
        };
        if (extra is not null)
        {
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extra)!)
                parameters[pair.Key] = pair.Value;
        }
        var node = new NodeDefinition("n1", "script.csharp", null, null, parameters);
        try
        {
            return await new CSharpScriptNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }
    }

    private static VisionValue Image(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));
}
