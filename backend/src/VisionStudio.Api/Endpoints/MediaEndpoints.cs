using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/media/status", (MediaLibraryService media) => Results.Ok(media.Status()));
        app.MapGet("/api/media/collections", (MediaLibraryService media) => Results.Ok(media.ListCollections()));
        app.MapGet("/api/media/items", (string? collection, string? label, int? take, MediaLibraryService media) =>
            Results.Ok(media.ListItems(collection, label, take ?? 200)));

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

        app.MapPost("/api/media/collections", (MediaCollectionCreateRequest request, MediaLibraryService media) =>
            Results.Ok(media.CreateCollection(request.Name)))
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
        }).RequireEngineer("media.import", "media").RequireRateLimiting("media-import");

        app.MapDelete("/api/media/items", (string path, MediaLibraryService media, ProductionRuntimeService production) =>
        {
            production.EnsureMediaMutationAllowed(media.ToMediaUri(path));
            media.DeleteItem(path);
            return Results.NoContent();
        }).RequireEngineer("media.item.delete", "media");

        return app;
    }
}

public sealed record MediaCollectionCreateRequest(string Name);
