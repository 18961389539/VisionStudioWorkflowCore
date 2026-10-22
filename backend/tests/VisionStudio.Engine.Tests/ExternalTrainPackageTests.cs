using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.ExternalTrain;

namespace VisionStudio.Engine.Tests;

public sealed class ExternalTrainPackageTests
{
    [Fact]
    public void Import_RejectsMissingManifest()
    {
        var src = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(src, "model.onnx"), "not-a-model");
            var dest = TempDir();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ExternalTrainPackage.Import(src, dest, expectedKind: ExternalTrainKind.Classification));
            Assert.Contains("manifest.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void Import_RejectsChecksumMismatch()
    {
        var src = TempDir();
        var destRoot = TempDir();
        try
        {
            WriteOnnxPackage(src, "model.onnx", "tiny-onnx-bytes", shaOverride: "deadbeef");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ExternalTrainPackage.Import(src, destRoot, expectedKind: ExternalTrainKind.Classification));
            Assert.Contains("Checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(src); TryDelete(destRoot); }
    }

    [Fact]
    public void Import_AcceptsClassificationStructure_AndSmokePassesWithoutSession()
    {
        var src = TempDir();
        var destRoot = TempDir();
        try
        {
            WriteOnnxPackage(src, "model.onnx", new string('x', 128), kind: ExternalTrainKind.Classification, labels: ["ok", "ng"]);
            var imported = ExternalTrainPackage.Import(src, destRoot, packageId: "cls-ok", expectedKind: ExternalTrainKind.Classification);
            Assert.Equal("cls-ok", imported.PackageId);
            Assert.Equal(ExternalTrainKind.Classification, imported.Manifest.Kind);
            var smoke = ExternalTrainPackage.SmokeEvaluate(imported.DestinationPath, openOnnxSession: false);
            Assert.True(smoke.Passed, string.Join("; ", smoke.Failures));
            var paths = ExternalTrainPackage.ResolveRuntimePaths(imported.DestinationPath);
            Assert.EndsWith("model.onnx", paths.ModelPath);
            Assert.Equal("ok,ng", paths.LabelsCsv);
        }
        finally { TryDelete(src); TryDelete(destRoot); }
    }

    [Fact]
    public void Import_AcceptsAnomalyPackage()
    {
        var src = TempDir();
        var destRoot = TempDir();
        try
        {
            WriteOnnxPackage(src, "anomaly.onnx", new string('a', 128), kind: ExternalTrainKind.Anomaly, labels: null);
            var imported = ExternalTrainPackage.Import(src, destRoot, expectedKind: ExternalTrainKind.Anomaly);
            Assert.Equal(ExternalTrainKind.Anomaly, imported.Manifest.Kind);
            Assert.True(ExternalTrainPackage.SmokeEvaluate(imported.DestinationPath).Passed);
        }
        finally { TryDelete(src); TryDelete(destRoot); }
    }

    [Fact]
    public void Import_AcceptsOcrPackage_AndRejectsWrongKind()
    {
        var src = TempDir();
        var destRoot = TempDir();
        try
        {
            WriteOcrPackage(src, "eng");
            var imported = ExternalTrainPackage.Import(src, destRoot, packageId: "ocr-eng", expectedKind: ExternalTrainKind.Ocr);
            Assert.Equal(ExternalTrainKind.Ocr, imported.Manifest.Kind);
            Assert.True(ExternalTrainPackage.SmokeEvaluate(imported.DestinationPath).Passed);
            Assert.NotNull(ExternalTrainPackage.ResolveRuntimePaths(imported.DestinationPath).DataPath);

            var bad = Assert.Throws<InvalidOperationException>(() =>
                ExternalTrainPackage.Import(src, TempDir(), expectedKind: ExternalTrainKind.Classification));
            Assert.Contains("does not match expected", bad.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(src); TryDelete(destRoot); }
    }

    [Fact]
    public void Import_AcceptsReferenceComparePackage_AndLoadsImages()
    {
        var src = TempDir();
        var destRoot = TempDir();
        try
        {
            WriteReferencePackage(src, width: 32, height: 24, count: 2);
            var imported = ExternalTrainPackage.Import(src, destRoot, expectedKind: ExternalTrainKind.ReferenceCompare);
            var smoke = ExternalTrainPackage.SmokeEvaluate(imported.DestinationPath);
            Assert.True(smoke.Passed, string.Join("; ", smoke.Failures));
            var paths = ExternalTrainPackage.ResolveRuntimePaths(imported.DestinationPath);
            Assert.Equal(2, paths.ReferencePaths.Count);
            Assert.All(paths.ReferencePaths, p => Assert.True(File.Exists(p)));
        }
        finally { TryDelete(src); TryDelete(destRoot); }
    }

    [Fact]
    public void Import_FromZip_AcceptsOcrPackage()
    {
        var src = TempDir();
        var destRoot = TempDir();
        var zip = Path.Combine(Path.GetTempPath(), "ocr-pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            WriteOcrPackage(src, "eng");
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zip);
            var imported = ExternalTrainPackage.Import(zip, destRoot, expectedKind: ExternalTrainKind.Ocr);
            Assert.True(ExternalTrainPackage.SmokeEvaluate(imported.DestinationPath).Passed);
        }
        finally
        {
            TryDelete(src); TryDelete(destRoot);
            try { File.Delete(zip); } catch { }
        }
    }

    [Fact]
    public void Smoke_RejectsTinyOnnx()
    {
        var src = TempDir();
        try
        {
            WriteOnnxPackage(src, "model.onnx", "tiny", kind: ExternalTrainKind.Classification, labels: ["a"]);
            // Validate checksums ok but smoke size check fails
            var smoke = ExternalTrainPackage.SmokeEvaluate(src, openOnnxSession: false);
            Assert.False(smoke.Passed);
            Assert.Contains(smoke.Failures, f => f.Contains("too small", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(src); }
    }

    private static void WriteOnnxPackage(string dir, string modelFile, string modelBytes, ExternalTrainKind kind = ExternalTrainKind.Classification, IReadOnlyList<string>? labels = null, string? modelFileName = null, string? shaOverride = null)
    {
        modelFileName ??= modelFile;
        var modelPath = Path.Combine(dir, modelFileName);
        File.WriteAllText(modelPath, modelBytes, Encoding.ASCII);
        var sha = shaOverride ?? ExternalTrainPackage.ComputeSha256Hex(modelPath);
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1",
            ["kind"] = kind.ToString(),
            ["name"] = kind + "-fixture",
            ["modelFile"] = modelFileName,
            ["inputWidth"] = 64,
            ["inputHeight"] = 64,
            ["fileSha256"] = new Dictionary<string, string> { [modelFileName] = sha }
        };
        if (labels is not null) manifest["labels"] = labels;
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void WriteOcrPackage(string dir, string language)
    {
        var file = $"{language}.traineddata";
        var path = Path.Combine(dir, file);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('T', 64)));
        var sha = ExternalTrainPackage.ComputeSha256Hex(path);
        var manifest = new
        {
            schemaVersion = "1",
            kind = "Ocr",
            name = "ocr-" + language,
            languages = new[] { language },
            fileSha256 = new Dictionary<string, string> { [file] = sha }
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void WriteReferencePackage(string dir, int width, int height, int count)
    {
        var refs = new List<string>();
        var hashes = new Dictionary<string, string>();
        for (var i = 1; i <= count; i++)
        {
            var name = i == 1 ? "reference.png" : $"reference{i}.png";
            var path = Path.Combine(dir, name);
            using var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(40 + i));
            Cv2.ImWrite(path, mat);
            refs.Add(name);
            hashes[name] = ExternalTrainPackage.ComputeSha256Hex(path);
        }
        var manifest = new
        {
            schemaVersion = "1",
            kind = "ReferenceCompare",
            name = "golden-fixture",
            references = refs,
            fileSha256 = hashes
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "vs-exttrain-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
