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
        app.MapGet("/api/media/sources", (MediaLibraryService media) => Results.Ok(media.ListSources()))
            .Produces<IReadOnlyList<MediaSourceDescriptor>>()
            .RequireEngineer("media.source.read", "media");
        app.MapGet("/api/media/linked-sources", (MediaLibraryService media) => Results.Ok(media.ListLinkedSources()))
            .Produces<IReadOnlyList<MediaLinkedSourceDescriptor>>()
            .RequireEngineer("media.link.read", "media");
        // 分页 + 总数（2026-11-13）：此前只支持 take，图库超过一页后旧图无法翻到。
        // 与 /api/alarms 保持一致：响应体仍是数组（前端渲染不变），总数放 X-Total-Count。
        app.MapGet("/api/media/items", (
            string? collection,
            string? label,
            string? search,
            int? minWidth,
            int? maxWidth,
            DateTimeOffset? modifiedAfter,
            DateTimeOffset? modifiedBefore,
            int? skip,
            int? take,
            HttpContext http,
            MediaLibraryService media) =>
        {
            var page = media.ListItems(
                collection,
                label,
                skip ?? 0,
                take ?? 200,
                search,
                minWidth,
                maxWidth,
                modifiedAfter,
                modifiedBefore);
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

        app.MapGet("/api/media/thumbnail", async (
            string path,
            MediaLibraryService media,
            CancellationToken ct) =>
        {
            var physical = await media.ResolveThumbnailPathAsync(path, ct);
            return Results.File(physical, "image/jpeg", enableRangeProcessing: false);
        }).Produces(StatusCodes.Status200OK);

        app.MapPost("/api/media/collections", async (MediaCollectionCreateRequest request, MediaLibraryService media, CancellationToken ct) =>
            Results.Ok(await media.CreateCollectionAsync(request.Name, ct)))
            .Produces<MediaCollectionDescriptor>()
            .RequireEngineer("media.collection.create", "media");

        app.MapPost("/api/media/linked-sources", async (
            MediaLinkedSourceCreateRequest request,
            MediaLibraryService media,
            ProductionRuntimeService production,
            HttpContext http,
            CancellationToken ct) =>
        {
            production.EnsureMediaMutationAllowed(request.Path);
            AuditContext.SetTarget(http, request.Name);
            return Results.Ok(await media.LinkDirectoryAsync(
                request.Name,
                request.Path,
                ct,
                http.User.Identity?.Name));
        })
            .Produces<MediaLinkedSourceDescriptor>()
            .RequireEngineer("media.link.create", "media");

        app.MapPost("/api/media/import", async (
            HttpRequest request,
            HttpContext http,
            MediaLibraryService media,
            ProductionRuntimeService production,
            CancellationToken ct) =>
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
            AuditContext.SetTarget(http, $"{collection}:{form.Files.Count}");

            var imported = new List<MediaItemDescriptor>();
            var relativePaths = form["relativePaths"].ToArray();
            if (relativePaths.Length != 0 && relativePaths.Length != form.Files.Count)
                throw new ApiValidationException("relativePaths count must match uploaded file count.");
            for (var index = 0; index < form.Files.Count; index++)
            {
                var file = form.Files[index];
                await using var stream = file.OpenReadStream();
                var relativePath = relativePaths.Length == 0
                    ? file.FileName
                    : relativePaths[index]?.ToString() ?? file.FileName;
                imported.Add(await media.ImportAsync(
                    collection,
                    label,
                    relativePath,
                    stream,
                    file.Length,
                    ct,
                    idempotentRelativePath: true,
                    importedBy: http.User.Identity?.Name));
            }
            return Results.Ok(imported);
        }).Produces<IReadOnlyList<MediaItemDescriptor>>().RequireEngineer("media.import", "media").RequireRateLimiting("media-import");

        app.MapDelete("/api/media/linked-sources", async (
            string name,
            MediaLibraryService media,
            ProductionRuntimeService production,
            CancellationToken ct) =>
        {
            production.EnsureMediaMutationAllowed($"media://linked/{name}");
            return await media.UnlinkDirectoryAsync(name, ct) ? Results.NoContent() : Results.NotFound();
        })
            .RequireEngineer("media.link.delete", "media");

        app.MapDelete("/api/media/items", async (string path, MediaLibraryService media, ProductionRuntimeService production, CancellationToken ct) =>
        {
            production.EnsureMediaMutationAllowed(media.ToMediaUri(path));
            await media.DeleteItemAsync(path, ct);
            return Results.NoContent();
        }).RequireEngineer("media.item.delete", "media");

        app.MapPost("/api/media/bulk", async (
            MediaBulkRequest request,
            MediaLibraryService media,
            ProductionRuntimeService production,
            HttpContext http,
            CancellationToken ct) =>
        {
            foreach (var path in request.Paths)
                production.EnsureMediaMutationAllowed(media.ToMediaUri(path));
            AuditContext.SetTarget(http, $"{request.Action}:{request.Paths.Length}");
            return Results.Ok(await media.BulkUpdateAsync(request, ct));
        })
            .Produces<MediaBulkResult>()
            .RequireEngineer("media.bulk", "media");

        app.MapGet("/api/media/manifest", async (
            string collection,
            MediaLibraryService media,
            CancellationToken ct) =>
            Results.Ok(await media.CreateManifestAsync(collection, ct)))
            .Produces<MediaManifest>()
            .RequireEngineer("media.manifest.read", "media");

        app.MapPost("/api/media/manifest/verify", async (
            string collection,
            MediaManifest manifest,
            MediaLibraryService media,
            CancellationToken ct) =>
            Results.Ok(await media.VerifyManifestAsync(collection, manifest, ct)))
            .Produces<MediaManifestVerification>()
            .RequireEngineer("media.manifest.verify", "media");

        app.MapGet("/api/media/integrity", async (
            MediaLibraryService media,
            CancellationToken ct) =>
            Results.Ok(await media.AuditIntegrityAsync(ct)))
            .Produces<MediaIntegrityReport>()
            .RequireEngineer("media.integrity.read", "media");

        app.MapPost("/api/media/maintenance", async (
            HttpContext http,
            MediaLibraryService media,
            CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, "media-index+thumbnails");
            return Results.Ok(await media.RepairIndexAsync(ct));
        })
            .Produces<MediaMaintenanceResult>()
            .RequireAdministrator("media.maintenance", "media");

        return app;
    }
}

public sealed record MediaCollectionCreateRequest(string Name);
public sealed record MediaLinkedSourceCreateRequest(string Name, string Path);
