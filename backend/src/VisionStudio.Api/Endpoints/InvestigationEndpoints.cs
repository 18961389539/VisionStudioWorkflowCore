using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public static class InvestigationEndpoints
{
    public static IEndpointRouteBuilder MapInvestigationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/investigations", async (int? take, string? status, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(take ?? 100, status, ct)))
            .Produces<IReadOnlyList<InvestigationCaseSummary>>();

        app.MapGet("/api/investigations/trends", async (int? days, int? top, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.TrendsAsync(days ?? 30, top ?? 8, ct)))
            .Produces<InvestigationTrendDashboard>();

        app.MapGet("/api/investigations/for-trace/{runId}", async (string runId, InvestigationCaseService service, CancellationToken ct) =>
        {
            var item = await service.GetForTraceAsync(runId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }).Produces<InvestigationCaseDetail>();

        app.MapGet("/api/investigations/{id}", async (string id, InvestigationCaseService service, CancellationToken ct) =>
        {
            var item = await service.GetAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }).Produces<InvestigationCaseDetail>();

        app.MapPost("/api/investigations/from-trace/{runId}", async (string runId, TrackInvestigationRequest? request, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.TrackFromTraceAsync(runId, request, ct)))
            .Produces<InvestigationCaseDetail>().RequireEngineer("investigation.track", "investigation");

        app.MapPut("/api/investigations/{id}", async (string id, UpdateInvestigationCaseRequest request, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, request, ct)))
            .Produces<InvestigationCaseDetail>().RequireEngineer("investigation.update", "investigation");

        app.MapGet("/api/investigations/{id}/verification-evidence", async (string id, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.GetVerificationEvidenceAsync(id, ct)))
            .Produces<IReadOnlyList<InvestigationVerificationEvidence>>()
            .RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/investigations/{id}/verification-evidence", async (string id, CreateInvestigationVerificationRequest request, InvestigationCaseService service, CancellationToken ct) =>
            Results.Ok(await service.EvaluateVerificationAsync(id, request, ct)))
            .Produces<InvestigationVerificationEvidence>()
            .RequireEngineer("investigation.verification.evaluate", "investigation");

        return app;
    }
}
