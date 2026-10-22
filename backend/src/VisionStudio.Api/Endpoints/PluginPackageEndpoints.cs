using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class PluginPackageEndpoints
{
    public static IEndpointRouteBuilder MapPluginPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plugin-packages", (PluginPackageService packages) => Results.Ok(packages.ListPackages()));
        app.MapGet("/api/plugin-publishers", (PluginPackageService packages) => Results.Ok(packages.ListTrustedPublishers()));

        app.MapPost("/api/plugin-packages/preflight", async (HttpRequest request, PluginPackageService packages, CancellationToken ct) =>
        {
            var (bytes, fileName) = await ReadSingleFileAsync(request, packages, ct);
            return Results.Ok(packages.Preflight(bytes, fileName));
        }).RequireEngineer("plugin.package.preflight", "plugin-package");

        app.MapPost("/api/plugin-packages/install", async (HttpRequest request, PluginPackageService packages, PluginManager plugins, ProductionRuntimeService production, CancellationToken ct) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before installing or activating plugin packages.");
            var (bytes, fileName) = await ReadSingleFileAsync(request, packages, ct);
            var result = packages.Install(bytes, fileName);
            if (result.ActivatedImmediately) plugins.Rescan();
            return Results.Ok(result);
        }).RequireAdministrator("plugin.package.install", "plugin-package");

        app.MapPost("/api/plugin-packages/{id}/versions/{version}/select", (string id, string version, PluginPackageService packages, PluginManager plugins, ProductionRuntimeService production) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before selecting a plugin package version.");
            var result = packages.SelectVersion(id, version);
            if (!result.RestartRequired) plugins.Rescan();
            return Results.Ok(result);
        }).RequireAdministrator("plugin.package.select-version", "plugin-package");

        app.MapPost("/api/plugin-packages/{id}/rollback", (string id, PluginRollbackRequest request, PluginPackageService packages, ProductionRuntimeService production) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before staging a plugin rollback.");
            return Results.Ok(packages.Rollback(id, request.TargetVersion));
        }).RequireAdministrator("plugin.package.rollback", "plugin-package");

        app.MapDelete("/api/plugin-packages/{id}/versions/{version}", (string id, string version, PluginPackageService packages, ProductionRuntimeService production) =>
        {
            if (production.Status.ProductionLocked)
                throw new ApiConflictException("Stop Production Runtime before removing an installed plugin version.");
            packages.RemoveVersion(id, version);
            return Results.NoContent();
        }).RequireAdministrator("plugin.package.remove-version", "plugin-package");

        app.MapPost("/api/plugin-publishers/trust", async (HttpRequest request, PluginPackageService packages, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) throw new ApiValidationException("Publisher trust import requires multipart/form-data.");
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1) throw new ApiValidationException("Exactly one X.509 certificate file is required.");
            var file = form.Files[0];
            if (file.Length <= 0 || file.Length > 1024 * 1024) throw new ApiValidationException("Publisher certificate must be between 1 byte and 1 MiB.");
            await using var stream = file.OpenReadStream();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, ct);
            return Results.Ok(packages.TrustPublisher(output.ToArray(), form["displayName"].ToString()));
        }).RequireAdministrator("plugin.publisher.trust", "plugin-publisher");

        app.MapDelete("/api/plugin-publishers/{thumbprint}", (string thumbprint, PluginPackageService packages) =>
        {
            packages.RemoveTrustedPublisher(thumbprint);
            return Results.NoContent();
        }).RequireAdministrator("plugin.publisher.untrust", "plugin-publisher");

        return app;
    }

    private static async Task<(byte[] Bytes, string FileName)> ReadSingleFileAsync(HttpRequest request, PluginPackageService packages, CancellationToken ct)
    {
        if (!request.HasFormContentType) throw new ApiValidationException("Plugin package request requires multipart/form-data.");
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1) throw new ApiValidationException("Exactly one .vspkg file is required.");
        var file = form.Files[0];
        if (!file.FileName.EndsWith(".vspkg", StringComparison.OrdinalIgnoreCase) && !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("Plugin package must use .vspkg (ZIP-compatible) format.");
        if (file.Length <= 0) throw new ApiValidationException("Plugin package is empty.");
        await using var stream = file.OpenReadStream();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return (output.ToArray(), file.FileName);
    }
}

public sealed record PluginRollbackRequest(string? TargetVersion);
