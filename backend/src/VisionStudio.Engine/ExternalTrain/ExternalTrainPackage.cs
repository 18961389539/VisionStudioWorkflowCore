using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace VisionStudio.Engine.ExternalTrain;

/// <summary>
/// External-train package kinds. Import validates vendor/offline-trained assets; this product does
/// not run SGD/backprop trainers. Caps vs HALCON train remain honest.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalTrainKind
{
    Ocr,
    Classification,
    Anomaly,
    ReferenceCompare,
    Segmentation,
    InstanceSegmentation,
    Pose,
    Obb
}

public sealed record ExternalTrainManifest(
    string SchemaVersion,
    ExternalTrainKind Kind,
    string Name,
    string? Description,
    string? ModelFile,
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? Labels,
    IReadOnlyList<string>? References,
    int? InputWidth,
    int? InputHeight,
    IReadOnlyDictionary<string, string>? FileSha256,
    string? SmokeImage,
    IReadOnlyDictionary<string, JsonElement>? Preprocess);

public sealed record ExternalTrainImportResult(
    string PackageId,
    string DestinationPath,
    ExternalTrainManifest Manifest,
    IReadOnlyList<string> FilesCopied,
    string DiagnosticsJson);

public sealed record ExternalTrainSmokeResult(
    bool Passed,
    string PackagePath,
    ExternalTrainKind Kind,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Failures,
    string DiagnosticsJson);

/// <summary>Import / validate / smoke-evaluate externally trained model and golden packages.</summary>
public static class ExternalTrainPackage
{
    public const string ManifestFileName = "manifest.json";
    public const string CurrentSchemaVersion = "1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ExternalTrainManifest ReadManifest(string packageRoot)
    {
        var root = RequireDirectory(packageRoot);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"External-train package requires {ManifestFileName} in '{root}'.");
        ExternalTrainManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExternalTrainManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"External-train manifest is not valid JSON: {ex.Message}", ex);
        }
        if (manifest is null)
            throw new InvalidOperationException("External-train manifest deserialized to null.");
        ValidateManifestShape(manifest);
        return manifest;
    }

    /// <summary>Validate package in place (directory or extracted zip root). Does not copy.</summary>
    public static ExternalTrainManifest Validate(string packageRoot, ExternalTrainKind? expectedKind = null, bool verifyChecksums = true)
    {
        var root = RequireDirectory(packageRoot);
        var manifest = ReadManifest(root);
        if (expectedKind is not null && manifest.Kind != expectedKind)
            throw new InvalidOperationException($"Package kind '{manifest.Kind}' does not match expected '{expectedKind}'.");
        ValidateRequiredFiles(root, manifest);
        if (verifyChecksums) VerifyChecksums(root, manifest);
        return manifest;
    }

    /// <summary>
    /// Copy a validated package into <paramref name="destinationRoot"/>/<paramref name="packageId"/>.
    /// Source may be a directory or a .zip file.
    /// </summary>
    public static ExternalTrainImportResult Import(
        string sourcePath,
        string destinationRoot,
        string? packageId = null,
        ExternalTrainKind? expectedKind = null,
        bool verifyChecksums = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new InvalidOperationException("External-train import requires a source path.");
        if (string.IsNullOrWhiteSpace(destinationRoot)) throw new InvalidOperationException("External-train import requires a destination root.");
        Directory.CreateDirectory(destinationRoot);
        var id = string.IsNullOrWhiteSpace(packageId) ? $"pkg-{Guid.NewGuid():N}" : packageId.Trim();
        if (id.Length is < 1 or > 128 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("packageId must be 1-128 characters and a valid file name.");
        var dest = Path.Combine(Path.GetFullPath(destinationRoot), id);
        if (Directory.Exists(dest) || File.Exists(dest))
            throw new InvalidOperationException($"Destination package '{dest}' already exists.");

        var staging = Path.Combine(Path.GetTempPath(), "vs-exttrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var fullSource = Path.GetFullPath(sourcePath);
            if (File.Exists(fullSource) && fullSource.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                System.IO.Compression.ZipFile.ExtractToDirectory(fullSource, staging);
            else if (Directory.Exists(fullSource))
                CopyDirectory(fullSource, staging);
            else
                throw new InvalidOperationException($"External-train source '{fullSource}' is not a directory or .zip file.");

            // If zip had a single top-level folder, descend into it.
            var root = ResolvePackageRoot(staging);
            var manifest = Validate(root, expectedKind, verifyChecksums);
            Directory.CreateDirectory(dest);
            CopyDirectory(root, dest);
            var copied = Directory.GetFiles(dest, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(dest, path).Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var diag = JsonSerializer.Serialize(new
            {
                packageId = id,
                kind = manifest.Kind.ToString(),
                name = manifest.Name,
                fileCount = copied.Length,
                destination = dest
            }, JsonOptions);
            return new ExternalTrainImportResult(id, dest, manifest, copied, diag);
        }
        catch
        {
            if (Directory.Exists(dest))
            {
                try { Directory.Delete(dest, true); } catch { /* best effort */ }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Structure + checksum smoke. Optional OpenCV load for reference packages and optional ONNX session open.
    /// Does not train. Held-out inference runs only when a smoke image is present and openSession is true.
    /// </summary>
    public static ExternalTrainSmokeResult SmokeEvaluate(string packageRoot, bool openOnnxSession = false)
    {
        var checks = new List<string>();
        var failures = new List<string>();
        string root;
        ExternalTrainManifest manifest;
        try
        {
            root = RequireDirectory(packageRoot);
            manifest = Validate(root, verifyChecksums: true);
            checks.Add("manifest");
            checks.Add("required-files");
            checks.Add("checksums");
        }
        catch (Exception ex)
        {
            return new ExternalTrainSmokeResult(false, packageRoot, ExternalTrainKind.Classification,
                checks, [ex.Message], JsonSerializer.Serialize(new { passed = false, error = ex.Message }, JsonOptions));
        }

        try
        {
            switch (manifest.Kind)
            {
                case ExternalTrainKind.Ocr:
                    foreach (var lang in manifest.Languages!)
                    {
                        var file = Path.Combine(root, $"{lang}.traineddata");
                        var info = new FileInfo(file);
                        if (info.Length < 16) throw new InvalidOperationException($"OCR traineddata '{lang}.traineddata' is too small ({info.Length} bytes).");
                        checks.Add($"ocr-file:{lang}");
                    }
                    break;
                case ExternalTrainKind.Classification:
                case ExternalTrainKind.Anomaly:
                case ExternalTrainKind.Segmentation:
                case ExternalTrainKind.InstanceSegmentation:
                case ExternalTrainKind.Pose:
                case ExternalTrainKind.Obb:
                    var modelPath = Path.Combine(root, manifest.ModelFile ?? "model.onnx");
                    var modelInfo = new FileInfo(modelPath);
                    if (modelInfo.Length < 64) throw new InvalidOperationException($"ONNX model '{manifest.ModelFile}' is too small ({modelInfo.Length} bytes).");
                    checks.Add("onnx-size");
                    if (manifest.InputWidth is null or < 1 or > 4096 || manifest.InputHeight is null or < 1 or > 4096)
                        throw new InvalidOperationException("ONNX package manifest requires inputWidth/inputHeight in 1..4096.");
                    checks.Add("onnx-input-size");
                    if (manifest.Kind == ExternalTrainKind.Classification && (manifest.Labels is null || manifest.Labels.Count == 0))
                        throw new InvalidOperationException("Classification package requires a non-empty labels list.");
                    if (manifest.Kind == ExternalTrainKind.Classification) checks.Add($"labels:{manifest.Labels!.Count}");
                    if (openOnnxSession)
                    {
                        try
                        {
                            using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath);
                            if (session.InputMetadata.Count < 1)
                                throw new InvalidOperationException("ONNX model has no inputs.");
                            checks.Add("onnx-session");
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"ONNX session open failed: {ex.Message}", ex);
                        }
                    }
                    break;
                case ExternalTrainKind.ReferenceCompare:
                    var refs = manifest.References!;
                    Mat? first = null;
                    try
                    {
                        foreach (var name in refs)
                        {
                            var path = Path.Combine(root, name);
                            using var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
                            if (mat.Empty()) throw new InvalidOperationException($"Reference image '{name}' could not be decoded.");
                            if (first is null)
                            {
                                first = mat.Clone();
                                checks.Add($"reference-size:{first.Width}x{first.Height}x{first.Channels()}");
                            }
                            else if (mat.Width != first.Width || mat.Height != first.Height || mat.Type() != first.Type())
                                throw new InvalidOperationException($"Reference '{name}' size/type does not match the first reference.");
                            checks.Add($"reference-load:{name}");
                        }
                    }
                    finally { first?.Dispose(); }
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported package kind '{manifest.Kind}'.");
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        var passed = failures.Count == 0;
        var diag = JsonSerializer.Serialize(new
        {
            passed,
            kind = manifest.Kind.ToString(),
            name = manifest.Name,
            checks,
            failures
        }, JsonOptions);
        return new ExternalTrainSmokeResult(passed, root, manifest.Kind, checks, failures, diag);
    }

    /// <summary>Resolve runtime paths from an imported package for node parameters.</summary>
    public static (string? ModelPath, string? DataPath, string? LabelsCsv, IReadOnlyList<string> ReferencePaths) ResolveRuntimePaths(string packageRoot)
    {
        var root = RequireDirectory(packageRoot);
        var manifest = Validate(root, verifyChecksums: false);
        return manifest.Kind switch
        {
            ExternalTrainKind.Ocr => (null, root, null, Array.Empty<string>()),
            ExternalTrainKind.Classification or ExternalTrainKind.Anomaly or ExternalTrainKind.Segmentation or ExternalTrainKind.InstanceSegmentation or ExternalTrainKind.Pose or ExternalTrainKind.Obb => (
                Path.Combine(root, manifest.ModelFile ?? "model.onnx"),
                null,
                manifest.Labels is { Count: > 0 } ? string.Join(',', manifest.Labels) : null,
                Array.Empty<string>()),
            ExternalTrainKind.ReferenceCompare => (
                null, null, null,
                (manifest.References ?? Array.Empty<string>()).Select(name => Path.Combine(root, name)).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported package kind '{manifest.Kind}'.")
        };
    }

    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateManifestShape(ExternalTrainManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported external-train schemaVersion '{manifest.SchemaVersion}' (expected '{CurrentSchemaVersion}').");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120)
            throw new InvalidOperationException("External-train manifest name must be 1-120 characters.");
        switch (manifest.Kind)
        {
            case ExternalTrainKind.Ocr:
                if (manifest.Languages is null || manifest.Languages.Count == 0)
                    throw new InvalidOperationException("OCR package manifest requires languages[].");
                if (manifest.Languages.Any(l => string.IsNullOrWhiteSpace(l) || l.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')))
                    throw new InvalidOperationException("OCR language codes must be alphanumeric/underscore (e.g. eng, chi_sim).");
                break;
            case ExternalTrainKind.Classification:
            case ExternalTrainKind.Anomaly:
            case ExternalTrainKind.Segmentation:
            case ExternalTrainKind.InstanceSegmentation:
            case ExternalTrainKind.Pose:
            case ExternalTrainKind.Obb:
                if (string.IsNullOrWhiteSpace(manifest.ModelFile))
                    throw new InvalidOperationException("ONNX package manifest requires modelFile.");
                if (!manifest.ModelFile.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ONNX package modelFile must end with .onnx.");
                break;
            case ExternalTrainKind.ReferenceCompare:
                if (manifest.References is null || manifest.References.Count is < 1 or > 5)
                    throw new InvalidOperationException("ReferenceCompare package requires 1..5 references[].");
                break;
            default:
                throw new InvalidOperationException($"Unknown external-train kind '{manifest.Kind}'.");
        }
    }

    private static void ValidateRequiredFiles(string root, ExternalTrainManifest manifest)
    {
        IEnumerable<string> required = manifest.Kind switch
        {
            ExternalTrainKind.Ocr => manifest.Languages!.Select(l => $"{l}.traineddata"),
            ExternalTrainKind.Classification or ExternalTrainKind.Anomaly or ExternalTrainKind.Segmentation or ExternalTrainKind.InstanceSegmentation or ExternalTrainKind.Pose or ExternalTrainKind.Obb => [manifest.ModelFile!],
            ExternalTrainKind.ReferenceCompare => manifest.References!,
            _ => []
        };
        foreach (var relative in required)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
                throw new InvalidOperationException($"External-train package is missing required file '{relative}'.");
        }
        if (!string.IsNullOrWhiteSpace(manifest.SmokeImage))
        {
            var smoke = Path.Combine(root, manifest.SmokeImage);
            if (!File.Exists(smoke))
                throw new InvalidOperationException($"External-train package smokeImage '{manifest.SmokeImage}' was not found.");
        }
    }

    private static void VerifyChecksums(string root, ExternalTrainManifest manifest)
    {
        if (manifest.FileSha256 is null || manifest.FileSha256.Count == 0)
            throw new InvalidOperationException("External-train manifest requires fileSha256 for every packaged file entry.");
        foreach (var (relative, expected) in manifest.FileSha256)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid fileSha256 key '{relative}'.");
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
                throw new InvalidOperationException($"fileSha256 references missing file '{relative}'.");
            var actual = ComputeSha256Hex(path);
            if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checksum mismatch for '{relative}': expected {expected}, got {actual}.");
        }
    }

    private static string ResolvePackageRoot(string staging)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]) && File.Exists(Path.Combine(entries[0], ManifestFileName)))
            return entries[0];
        if (File.Exists(Path.Combine(staging, ManifestFileName))) return staging;
        // search one level
        foreach (var dir in Directory.GetDirectories(staging))
        {
            if (File.Exists(Path.Combine(dir, ManifestFileName))) return dir;
        }
        throw new InvalidOperationException("Could not find manifest.json at the package root.");
    }

    private static string RequireDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Package path is required.");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new InvalidOperationException($"Package directory '{full}' does not exist.");
        return full;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
