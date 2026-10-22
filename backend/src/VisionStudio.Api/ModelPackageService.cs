using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.ExternalTrain;

namespace VisionStudio.Api;

public sealed record ModelPackageSummary(string Id, string Kind, string Name, string? Description, string Path, DateTimeOffset ImportedAt, int FileCount);
public sealed record ModelPackageDetail(ModelPackageSummary Summary, ExternalTrainManifest Manifest, IReadOnlyList<string> Files);
public sealed record ModelPackageImportRequest(string SourcePath, string? PackageId = null, string? ExpectedKind = null);
public sealed record ModelPackageSmokeRequest(bool OpenOnnxSession = false);

/// <summary>Filesystem registry of imported external-train packages under data/model-packages.</summary>
public sealed class ModelPackageService(IWebHostEnvironment env)
{
    private readonly string _root = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "model-packages");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root => _root;

    public IReadOnlyList<ModelPackageSummary> List()
    {
        Directory.CreateDirectory(_root);
        var list = new List<ModelPackageSummary>();
        foreach (var dir in Directory.GetDirectories(_root).OrderByDescending(Directory.GetCreationTimeUtc))
        {
            try { list.Add(Summarize(dir)); }
            catch { /* skip corrupt */ }
        }
        return list;
    }

    public ModelPackageDetail Get(string id)
    {
        var path = ResolveId(id);
        var manifest = ExternalTrainPackage.ReadManifest(path);
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(path, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        return new ModelPackageDetail(Summarize(path), manifest, files);
    }

    public ExternalTrainManifest Preflight(string sourcePath, string? expectedKind = null)
    {
        var kind = ParseKind(expectedKind);
        // Import to temp via validate of extracted staging without persisting.
        var staging = Path.Combine(Path.GetTempPath(), "vs-preflight-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = ExternalTrainPackage.Import(sourcePath, staging, packageId: "preflight", expectedKind: kind, verifyChecksums: true);
            return result.Manifest;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    public ModelPackageDetail Import(ModelPackageImportRequest request)
    {
        Directory.CreateDirectory(_root);
        var kind = ParseKind(request.ExpectedKind);
        var imported = ExternalTrainPackage.Import(request.SourcePath, _root, request.PackageId, kind, verifyChecksums: true);
        return Get(imported.PackageId);
    }

    public ExternalTrainSmokeResult Smoke(string id, bool openOnnxSession = false)
    {
        var path = ResolveId(id);
        return ExternalTrainPackage.SmokeEvaluate(path, openOnnxSession);
    }

    public void Delete(string id)
    {
        var path = ResolveId(id);
        Directory.Delete(path, true);
    }

    private ModelPackageSummary Summarize(string path)
    {
        var manifest = ExternalTrainPackage.ReadManifest(path);
        var id = Path.GetFileName(path);
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
        return new ModelPackageSummary(id, manifest.Kind.ToString(), manifest.Name, manifest.Description, path,
            Directory.GetCreationTimeUtc(path), files);
    }

    private string ResolveId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
            throw new ApiValidationException("Invalid model package id.");
        var path = Path.Combine(_root, id.Trim());
        if (!Directory.Exists(path)) throw new ApiNotFoundException($"Model package '{id}' was not found.");
        return path;
    }

    private static ExternalTrainKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Enum.TryParse<ExternalTrainKind>(value.Trim(), ignoreCase: true, out var kind))
            throw new ApiValidationException($"expectedKind must be one of: {string.Join(',', Enum.GetNames<ExternalTrainKind>())}.");
        return kind;
    }
}
