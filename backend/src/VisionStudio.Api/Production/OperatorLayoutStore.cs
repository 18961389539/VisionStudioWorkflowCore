using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public enum OperatorWidget { Result, Counts, Timing, Alarms, Preview }

public sealed record OperatorLayout(string? Title, IReadOnlyList<OperatorWidget> Widgets)
{
    // 告警卡进默认布局：只有数量没有内容的告警卡对操作员没有意义，而默认不显示等于永远不看。
    public static OperatorLayout Default { get; } = new(null,
        [OperatorWidget.Result, OperatorWidget.Counts, OperatorWidget.Timing, OperatorWidget.Alarms]);
}

public sealed class OperatorLayoutStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly string _root = Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath, configuration), "production", "operator-layouts");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static string RevisionOf(OperatorLayout layout)
        => $"\"{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(layout, Json)))}\"";

    public async Task<OperatorLayout> GetAsync(string jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadAsync(PathFor(jobId), ct); }
        finally { _gate.Release(); }
    }

    public async Task<OperatorLayout> SaveAsync(string jobId, OperatorLayout layout, string expectedRevision, CancellationToken ct)
    {
        layout = Normalize(layout);
        var path = PathFor(jobId);
        var staging = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        await _gate.WaitAsync(ct);
        try
        {
            if (!string.Equals(expectedRevision, RevisionOf(await ReadAsync(path, ct)), StringComparison.Ordinal))
                throw new ApiConflictException("The operator layout changed. Reload it before saving.");

            Directory.CreateDirectory(_root);
            await using (var stream = File.Create(staging))
                await JsonSerializer.SerializeAsync(stream, layout, Json, ct);
            File.Move(staging, path, overwrite: true);
            return layout;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            finally { _gate.Release(); }
        }
    }

    private string PathFor(string jobId)
        => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))) + ".json");

    private static async Task<OperatorLayout> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return OperatorLayout.Default;
        await using var stream = File.OpenRead(path);
        var layout = await JsonSerializer.DeserializeAsync<OperatorLayout>(stream, Json, ct)
            ?? throw new InvalidDataException("The operator layout is empty.");
        return Normalize(layout);
    }

    private static OperatorLayout Normalize(OperatorLayout layout)
    {
        var title = layout.Title?.Trim();
        if (title?.Length > 60 || title?.Any(char.IsControl) == true)
            throw new ApiValidationException("The operator title must be single-line and at most 60 characters.");
        if (layout.Widgets is not { Count: >= 1 and <= 5 } ||
            layout.Widgets.Any(widget => !Enum.IsDefined(widget)) || layout.Widgets.Distinct().Count() != layout.Widgets.Count)
            throw new ApiValidationException("Choose 1 to 5 distinct operator widgets.");
        return new OperatorLayout(string.IsNullOrEmpty(title) ? null : title, layout.Widgets);
    }
}
