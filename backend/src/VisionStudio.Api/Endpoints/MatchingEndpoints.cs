using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OpenCvSharp;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Api.Endpoints;

/// <summary>轴对齐模板 ROI（训练缓冲与检测区域一致）；旋转 ROI 需先在编辑器里裁成轴对齐再调用。</summary>
public sealed record MatchingAutoTuneRoi(double X, double Y, double Width, double Height, double AngleDeg = 0);

public sealed record MatchingAutoTuneHttpRequest(
    string NodeType = "match.shape",
    string? TemplatePath = null,
    MatchingAutoTuneRoi? Roi = null,
    double AngleStartDeg = -180,
    double AngleRangeDeg = 360,
    double ScaleTolerancePercent = 0,
    string Metric = "use_polarity",
    int MinContrast = 5,
    int CurrentNumLevels = 0,
    double CurrentAngleStepDeg = 1,
    string Profile = "balanced",
    string? LockedParameters = null,
    string? Parameters = null);

public sealed record MatchingAutoTuneProvenance(
    string AutoTuneVersion,
    string NativeLibrary,
    string TemplateSha256,
    int TemplateWidth,
    int TemplateHeight,
    DateTimeOffset ComputedAt);

public sealed record MatchingAutoTuneHttpResponse(
    string NodeType,
    int PyramidLevels,
    double AngleStepDeg,
    int? Contrast,
    int MinContrast,
    string Optimization,
    MatchingModelInspect Inspect,
    IReadOnlyList<MatchingAutoTuneWarning> Warnings,
    IReadOnlyList<string> IgnoredParameters,
    IReadOnlyDictionary<string, string> Raw,
    IReadOnlyDictionary<string, object?> Apply,
    MatchingAutoTuneProvenance Provenance);

/// <summary>
/// 编排期自动调参入口。只读模板图 + 原生 determine，不改任何工作流；
/// 返回的 <c>apply</c> 由编辑器合并进节点参数（写死数值，运行期不再调用 determine）。
/// </summary>
public static class MatchingEndpoints
{
    private static readonly string[] AllowedExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];
    private const long MaxTemplateBytes = 64L * 1024 * 1024;

    public static IEndpointRouteBuilder MapMatchingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/matching/auto-tune", (
            MatchingAutoTuneHttpRequest request,
            IWebHostEnvironment environment,
            IConfiguration configuration) =>
        {
            try
            {
                return Results.Ok(Run(request, environment, configuration));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireEngineer("matching.auto-tune", "matching");

        return app;
    }

    internal static MatchingAutoTuneHttpResponse Run(
        MatchingAutoTuneHttpRequest request,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TemplatePath))
            throw new ApiValidationException("templatePath is required (inline/base64 templates are not supported yet).");

        var nodeType = NormalizeNodeType(request.NodeType);
        var profile = ParseProfile(request.Profile);
        var path = ResolveTemplatePath(request.TemplatePath, environment, configuration);

        using var source = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (source.Empty())
            throw new ApiValidationException($"Template image '{request.TemplatePath}' could not be decoded as a grayscale image.");

        using var template = Crop(source, request.Roi);

        var tuneRequest = new MatchingAutoTuneRequest(
            AngleStartDeg: request.AngleStartDeg,
            AngleRangeDeg: request.AngleRangeDeg,
            ScaleTolerancePercent: request.ScaleTolerancePercent,
            Metric: request.Metric,
            MinContrast: request.MinContrast,
            CurrentNumLevels: request.CurrentNumLevels,
            CurrentAngleStepDeg: request.CurrentAngleStepDeg,
            Profile: profile,
            LockedParameters: request.LockedParameters,
            Parameters: request.Parameters);

        var recommendation = nodeType == "match.template"
            ? MatchingAutoTune.DetermineNcc(template, tuneRequest)
            : MatchingAutoTune.DetermineShape(template, tuneRequest);

        var templateSha = Sha256(template);
        var reportId = Sha256Text($"{MatchingAutoTune.Version}|{nodeType}|{templateSha}|{JsonSerializer.Serialize(request)}")[..16];
        var apply = new Dictionary<string, object?>(recommendation.ToNodeParameters(), StringComparer.OrdinalIgnoreCase)
        {
            ["autoTuneReportId"] = reportId
        };

        return new MatchingAutoTuneHttpResponse(
            nodeType,
            recommendation.PyramidLevels,
            recommendation.AngleStepDeg,
            recommendation.Contrast,
            recommendation.MinContrast,
            recommendation.Optimization,
            recommendation.Inspect,
            recommendation.Warnings,
            recommendation.IgnoredParameters,
            recommendation.Raw,
            apply,
            new MatchingAutoTuneProvenance(
                MatchingAutoTune.Version,
                MatchingAutoTune.NativeLibraryVersion,
                templateSha,
                template.Width,
                template.Height,
                DateTimeOffset.Now));
    }

    private static string NormalizeNodeType(string? nodeType)
    {
        var value = string.IsNullOrWhiteSpace(nodeType) ? "match.shape" : nodeType.Trim().ToLowerInvariant();
        return value switch
        {
            "match.shape" or "match.template" => value,
            _ => throw new ApiValidationException($"nodeType must be match.shape or match.template (got '{nodeType}').")
        };
    }

    private static MatchingAutoTuneProfile ParseProfile(string? profile)
        => (profile ?? "balanced").Trim().ToLowerInvariant() switch
        {
            "balanced" => MatchingAutoTuneProfile.Balanced,
            "speed" => MatchingAutoTuneProfile.Speed,
            "accuracy" => MatchingAutoTuneProfile.Accuracy,
            _ => throw new ApiValidationException($"profile must be balanced|speed|accuracy (got '{profile}').")
        };

    private static Mat Crop(Mat source, MatchingAutoTuneRoi? roi)
    {
        if (roi is null) return source.Clone();
        if (!double.IsFinite(roi.X) || !double.IsFinite(roi.Y) || !double.IsFinite(roi.Width) || !double.IsFinite(roi.Height))
            throw new ApiValidationException("roi values must be finite.");
        if (Math.Abs(roi.AngleDeg) > 1e-9)
            throw new ApiValidationException("roi.angleDeg must be 0; pre-rotate the template into an axis-aligned crop before auto-tuning.");
        if (roi.Width < MatchingAutoTune.MinTemplateSize || roi.Height < MatchingAutoTune.MinTemplateSize)
            throw new ApiValidationException($"roi must be at least {MatchingAutoTune.MinTemplateSize}x{MatchingAutoTune.MinTemplateSize} px.");

        var x = (int)Math.Round(roi.X);
        var y = (int)Math.Round(roi.Y);
        var w = (int)Math.Round(roi.Width);
        var h = (int)Math.Round(roi.Height);
        if (x < 0 || y < 0 || x + w > source.Width || y + h > source.Height)
            throw new ApiValidationException($"roi ({x},{y},{w},{h}) exceeds the {source.Width}x{source.Height} template image.");
        return new Mat(source, new Rect(x, y, w, h)).Clone();
    }

    private static string ResolveTemplatePath(string path, IWebHostEnvironment environment, IConfiguration configuration)
    {
        // 节点侧 templatePath 按进程工作目录解析；API 先按内容根找，找不到再按 CWD 兜底，
        // 避免同一路径在编辑器与调试运行下解析到不同位置。
        var candidates = new[]
        {
            Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path)),
            Path.GetFullPath(path)
        };
        var full = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        if (!File.Exists(full))
            throw new ApiValidationException($"Template image '{path}' was not found.");

        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            throw new ApiValidationException($"Template image must be one of {string.Join(", ", AllowedExtensions)} (got '{extension}').");
        if (new FileInfo(full).Length > MaxTemplateBytes)
            throw new ApiValidationException($"Template image exceeds the {MaxTemplateBytes / (1024 * 1024)} MB limit.");

        var roots = AllowedRoots(environment, configuration);
        if (!roots.Any(root => IsUnder(full, root)))
            throw new ApiValidationException(
                "Template path must live under the application, data, media or working directory. " +
                "Configure Matching:AutoTune:AllowedRoots (semicolon separated) to add deployment roots.");

        return full;
    }

    private static IReadOnlyList<string> AllowedRoots(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var roots = new List<string>
        {
            Path.GetFullPath(environment.ContentRootPath),
            Path.GetFullPath(VisionStudioDataRoot.Resolve(environment.ContentRootPath)),
            Path.GetFullPath(VisionStudioDataRoot.ResolveMediaRoot(environment.ContentRootPath, configuration["MediaLibrary:RootPath"])),
            Path.GetFullPath(Directory.GetCurrentDirectory()),
            Path.GetFullPath(AppContext.BaseDirectory)
        };

        var configured = configuration["Matching:AutoTune:AllowedRoots"];
        if (!string.IsNullOrWhiteSpace(configured))
            roots.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Path.IsPathRooted)
                .Select(Path.GetFullPath));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsUnder(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Sha256Text(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
