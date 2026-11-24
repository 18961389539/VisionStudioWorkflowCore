using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class RecipeDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapRecipeDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/production/recipe-rollouts", async (RecipeRolloutService rollout, CancellationToken ct) =>
            Results.Ok(await rollout.ListAsync(ct)))
            .Produces<IReadOnlyList<RecipeRolloutBatch>>().RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/production/recipe-rollouts/latest-by-station", async (RecipeRolloutService rollout, CancellationToken ct) =>
            Results.Ok(await rollout.LatestByStationAsync(ct)))
            .Produces<IReadOnlyList<StationRecipeRollout>>().RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/production/recipe-rollouts", async (RecipeRolloutRequest request, RecipeRolloutService rollout,
            HttpContext http, CancellationToken ct) =>
        {
            var id = Guid.NewGuid().ToString("N");
            AuditContext.SetTarget(http, id);
            return Results.Ok(await rollout.SendAsync(id, request, ct));
        }).Produces<RecipeRolloutBatch>().RequireEngineer("production.recipe-rollout.send", "production");

        app.MapGet("/api/production/recipe-deliveries", async (JobStore jobs, CancellationToken ct) =>
            Results.Ok(await jobs.ListRecipeDeliveriesAsync(ct)))
            .Produces<IReadOnlyList<RecipeDeliveryReceipt>>().RequireAuthorization(SecurityPolicies.Engineer);

        app.MapPost("/api/production/recipe-deliveries", async (StationRecipeDelivery request, JobStore jobs,
            IOptions<ProductionFleetOptions> options, HttpContext http, CancellationToken ct) =>
        {
            if (options.Value.StationId is not { } stationId)
                throw new ApiNotFoundException("The receiving station identity is not configured.");
            if (!string.Equals(request.StationId, stationId, StringComparison.Ordinal))
                throw new ApiConflictException("The delivery was addressed to a different station.");
            AuditContext.SetTarget(http, $"{request.JobId}@source-v{request.SourceVersion}");
            return Results.Ok(await jobs.ReceiveDeliveryAsync(request, ct));
        })
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(RecipeRolloutService.MaxPackageBytes))
            .Produces<RecipeDeliveryReceipt>()
            .RequireAuthorization(SecurityPolicies.StationDelivery).WithAudit("production.recipe-delivery.receive", "production");

        app.MapPost("/api/production/recipe-deliveries/{id}/publish", async (string id, PublishRecipeDeliveryRequest request,
            HttpContext http, JobStore jobs, VisionWorkflowCompiler compiler, WorkflowModuleExpander modules,
            RuntimeDependencyManifestService dependencies, CancellationToken ct) =>
        {
            var receipt = await jobs.GetRecipeDeliveryAsync(id, ct)
                ?? throw new ApiNotFoundException("The staged recipe delivery was not found.");
            if (request.ExpectedUpdatedAt == default)
                throw new ApiValidationException("Review the received recipe and supply its current revision.");
            AuditContext.SetTarget(http, $"{receipt.JobId}@v{receipt.LocalVersion}/delivery-{id}");
            return await JobEndpoints.PublishVersionAsync(receipt.JobId, receipt.LocalVersion, request.AcknowledgeMaturityNodes,
                request.ExpectedUpdatedAt, http, jobs, compiler, modules, dependencies, ct);
        }).Produces<JobDescriptor>().Produces<NodeMaturityGate.AcknowledgementRequired>(StatusCodes.Status409Conflict)
            .RequireEngineer("production.recipe-delivery.publish", "production");

        return app;
    }
}
