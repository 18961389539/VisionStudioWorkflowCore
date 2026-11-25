using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed partial class JobStore
{
    internal async Task<StationRecipeDelivery> GetPublishedDeliveryAsync(RecipeRolloutRequest request, CancellationToken ct)
    {
        ValidateId(request.JobId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Jobs.FirstOrDefaultAsync(x => x.Id == request.JobId, ct)
            ?? throw new ApiNotFoundException($"Job '{request.JobId}' does not exist.");
        if (row.PublishedVersion != request.SourceVersion || string.IsNullOrEmpty(row.PublishedDependencyManifestHash))
            throw new ApiConflictException("The selected recipe version is no longer the published version. Reload it before sending.");

        var version = await GetVersionAsync(row.Id, request.SourceVersion, ct)
            ?? throw new ApiNotFoundException($"Published version for job '{row.Id}' does not exist.");
        if (!string.Equals(request.WorkflowHash, version.WorkflowHash, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("The selected workflow fingerprint changed. Reload the recipe before sending.");

        return new StationRecipeDelivery("", row.Id, row.Name, version.Version, version.WorkflowHash,
            RequiresDatasetValidation(row) || await DeliveredVersionRequiresValidationAsync(db, row.Id, version.Version, ct), version.Workflow);
    }

    internal async Task<RecipeDeliveryReceipt> ReceiveDeliveryAsync(StationRecipeDelivery delivery, CancellationToken ct)
    {
        ValidateId(delivery.JobId);
        if (delivery.SourceVersion <= 0 || string.IsNullOrWhiteSpace(delivery.JobName) ||
            delivery.Workflow?.Nodes is null || delivery.Workflow.Edges is null ||
            delivery.Workflow.Nodes.Any(node => node is null) || delivery.Workflow.Edges.Any(edge => edge is null))
            throw new ApiValidationException("The recipe delivery is incomplete.");

        WorkflowStore.EnsureSupportedSchema(delivery.Workflow, delivery.JobId);
        var hash = WorkflowFingerprint.Compute(delivery.Workflow);
        if (!string.Equals(hash, delivery.WorkflowHash, StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("The delivered workflow fingerprint does not match.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct, strongDurability: true);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);

        var previous = await (
            from r in db.RecipeDeliveryReceipts
            join j in db.Jobs on r.JobId equals j.Id
            where r.StationId == delivery.StationId && r.JobId == delivery.JobId &&
                  r.SourceVersion == delivery.SourceVersion && r.WorkflowHash == hash &&
                  r.RequiresValidation == delivery.RequiresValidation
            select ToReceipt(r, j))
            .FirstOrDefaultAsync(ct);
        if (previous is not null) return previous;

        var row = await db.Jobs.AsTracking().FirstOrDefaultAsync(x => x.Id == delivery.JobId, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is not null && now <= row.UpdatedAt) now = row.UpdatedAt.AddTicks(1);
        var localVersion = row is null ? 1 : checked(row.LatestVersion + 1);
        var name = row?.Name ?? delivery.JobName.Trim();
        if (row is null)
        {
            db.Jobs.Add(new JobEntity
            {
                Id = delivery.JobId,
                Name = name,
                Description = string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                LatestVersion = 1,
                RequiresValidation = delivery.RequiresValidation
            });
        }
        else
        {
            row.LatestVersion = localVersion;
            row.UpdatedAt = now;
        }

        var snapshot = new JobVersionSnapshot(delivery.JobId, localVersion, hash, now,
            $"Station delivery from source V{delivery.SourceVersion}", delivery.Workflow);
        AddVersionEntity(db, snapshot);
        var receiptId = Guid.NewGuid().ToString("N");
        db.RecipeDeliveryReceipts.Add(new RecipeDeliveryReceiptEntity
        {
            Id = receiptId,
            StationId = delivery.StationId,
            JobId = delivery.JobId,
            SourceVersion = delivery.SourceVersion,
            WorkflowHash = hash,
            LocalVersion = localVersion,
            RequiresValidation = delivery.RequiresValidation,
            ReceivedAt = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new RecipeDeliveryReceipt(receiptId, delivery.StationId, delivery.JobId, name, delivery.SourceVersion, hash,
            localVersion, delivery.RequiresValidation || (row is not null && RequiresDatasetValidation(row)), row?.PublishedVersion, now, now);
    }

    internal async Task<IReadOnlyList<RecipeDeliveryReceipt>> ListRecipeDeliveriesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await (
            from r in db.RecipeDeliveryReceipts
            join j in db.Jobs on r.JobId equals j.Id
            orderby r.ReceivedAt descending, r.Id descending
            select ToReceipt(r, j))
            .Take(30)
            .ToListAsync(ct);
    }

    internal async Task<RecipeDeliveryReceipt?> GetRecipeDeliveryAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await (
            from r in db.RecipeDeliveryReceipts
            join j in db.Jobs on r.JobId equals j.Id
            where r.Id == id
            select ToReceipt(r, j))
            .FirstOrDefaultAsync(ct);
    }

    private static RecipeDeliveryReceipt ToReceipt(RecipeDeliveryReceiptEntity r, JobEntity j)
        => new(r.Id, r.StationId, r.JobId, j.Name, r.SourceVersion, r.WorkflowHash, r.LocalVersion,
            r.RequiresValidation || RequiresDatasetValidation(j),
            j.PublishedVersion, j.UpdatedAt, r.ReceivedAt);

    private static bool RequiresDatasetValidation(JobEntity row)
        => row.RequiresValidation || !string.IsNullOrWhiteSpace(row.ProductId) || !string.IsNullOrWhiteSpace(row.RecipeCode);

    private static Task<bool> DeliveredVersionRequiresValidationAsync(VisionStudioDbContext db, string jobId, int version, CancellationToken ct)
        => db.RecipeDeliveryReceipts.AnyAsync(x => x.JobId == jobId && x.LocalVersion == version && x.RequiresValidation, ct);
}
