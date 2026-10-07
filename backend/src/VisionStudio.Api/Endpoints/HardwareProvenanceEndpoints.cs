using VisionStudio.Api.Provenance;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class HardwareProvenanceEndpoints
{
    public static IEndpointRouteBuilder MapHardwareProvenanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hardware-provenance", async (HardwareProvenanceService provenance, CancellationToken ct) =>
            Results.Ok(await provenance.ListAsync(ct)));

        app.MapGet("/api/hardware-provenance/{kind}/{id}", async (string kind, string id, HardwareProvenanceService provenance, CancellationToken ct) =>
            Results.Ok(await provenance.CaptureAsync(kind, id, ct)));

        app.MapPut("/api/hardware-provenance/{kind}/{id}", async (
            string kind, string id, HardwareProvenanceDeclaration request,
            HardwareProvenanceService provenance, ProductionRuntimeService production, CancellationToken ct) =>
        {
            kind = kind.Trim().ToLowerInvariant();
            production.EnsureDependencyMutationAllowed(kind, id);
            return Results.Ok(await provenance.UpsertAsync(kind, id, request, ct));
        }).RequireEngineer("hardware-provenance.update", "hardware-provenance");

        app.MapDelete("/api/hardware-provenance/{kind}/{id}", async (
            string kind, string id, HardwareProvenanceService provenance, ProductionRuntimeService production, CancellationToken ct) =>
        {
            kind = kind.Trim().ToLowerInvariant();
            production.EnsureDependencyMutationAllowed(kind, id);
            return await provenance.DeleteDeclarationAsync(kind, id, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireEngineer("hardware-provenance.delete", "hardware-provenance");

        return app;
    }
}
