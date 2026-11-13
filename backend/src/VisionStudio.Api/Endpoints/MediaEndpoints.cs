using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/media/status", (MediaLibraryService media) => Results.Ok(media.Status()))
            .Produces<MediaLibraryStatus>();
        app.MapGet("/api/media/collections", (MediaLibraryService media) => Results.Ok(media.ListCollections()))
            .Produces<IReadOnlyList<MediaCollectionDescriptor>>();
        // 分页 + 总数（2026-11-13）：此前只支持 take，图库超过一页后旧图无法翻到。
        // 与 /api/alarms 保持一致：响应体仍是数组（前端渲染不变），总数放 X-Total-Count。
        app.MapGet("/api/media/items", (string? collection, string? label, int? skip, int? take, HttpContext http, MediaLibraryService media) =>
        {
            var page = media.ListItems(collection, label, skip ?? 0, take ?? 200);
            http.Response.Headers["X-Total-Count"] = page.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(page.Items);
        })
            .Produces<IReadOnlyList<MediaItemDescriptor>>();

        app.MapGet("/api/media/preview", (string path, MediaLibraryService media) =>
        {
            var physical = media.ResolveItemPath(path);
            var contentType = Path.GetExtension(physical).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => "image/jpeg"
            };
            return Results.File(physical, contentType, enableRangeProcessing: false);
        });

        app.MapPost("/api/media/collections", async (MediaCollectionCreateRequest request, MediaLibraryService media, CancellationToken ct) =>
            Results.Ok(await media.CreateCollectionAsync(request.Name, ct)))
            .Produces<MediaCollectionDescriptor>()
            .RequireEngineer("media.collection.create", "media");

        app.MapPost("/api/media/import", async (HttpRequest request, MediaLibraryService media, ProductionRuntimeService production, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) throw new ApiValidationException("Media import requires multipart/form-data.");
            if (request.ContentLength is > 0) media.EnsureIncomingRequestCapacity(request.ContentLength.Value);
            var form = await request.ReadFormAsync(ct);
            var collection = form["collection"].ToString();
            var label = form["label"].ToString();
            if (string.IsNullOrWhiteSpace(collection)) throw new ApiValidationException("collection is required.");
            if (form.Files.Count == 0) throw new ApiValidationException("At least one image file is required.");
            if (form.Files.Count > media.MaxBatchFiles) throw new ApiValidationException($"Media import batch exceeds MaxBatchFiles ({media.MaxBatchFiles}).");
            var batchBytes = form.Files.Sum(x => x.Length);
            if (batchBytes > media.MaxBatchBytes) throw new ApiValidationException($"Media import batch exceeds MaxBatchBytes ({media.MaxBatchBytes}).");
            production.EnsureMediaMutationAllowed(media.ToMediaUri(collection));

            var imported = new List<MediaItemDescriptor>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                imported.Add(await media.ImportAsync(collection, label, file.FileName, stream, file.Length, ct));
            }
            return Results.Ok(imported);
        }).Produces<IReadOnlyList<MediaItemDescriptor>>().RequireEngineer("media.import", "media").RequireRateLimiting("media-import");

        app.MapDelete("/api/media/items", async (string path, MediaLibraryService media, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureMediaMutationAllowed(media.ToMediaUri(path));
            await media.DeleteItemAsync(path, ct);
            return Results.NoContent();
        }).RequireEngineer("media.item.delete", "media");

        return app;
    }
}

public sealed record MediaCollectionCreateRequest(string Name);
