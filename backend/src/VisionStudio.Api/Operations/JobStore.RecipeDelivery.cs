using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed partial class JobStore
{
    private const string RecipeReceiptSelect = """
SELECT r.id,r.station_id,r.job_id,j.name,r.source_version,r.workflow_hash,r.local_version,
       CASE WHEN r.requires_validation <> 0 OR j.requires_validation <> 0 OR
                 COALESCE(j.product_id, '') <> '' OR COALESCE(j.recipe_code, '') <> '' THEN 1 ELSE 0 END,
       j.published_version,j.updated_at,r.received_at
FROM recipe_delivery_receipts r JOIN jobs j ON j.id=r.job_id
""";

    internal async Task<StationRecipeDelivery> GetPublishedDeliveryAsync(RecipeRolloutRequest request, CancellationToken ct)
    {
        ValidateId(request.JobId);
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadJobRowAsync(connection, request.JobId, null, ct)
            ?? throw new ApiNotFoundException($"Job '{request.JobId}' does not exist.");
        if (row.PublishedVersion != request.SourceVersion || string.IsNullOrEmpty(row.PublishedDependencyManifestHash))
            throw new ApiConflictException("The selected recipe version is no longer the published version. Reload it before sending.");

        var version = await GetVersionAsync(row.Id, request.SourceVersion, ct)
            ?? throw new ApiNotFoundException($"Published version for job '{row.Id}' does not exist.");
        if (!string.Equals(request.WorkflowHash, version.WorkflowHash, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("The selected workflow fingerprint changed. Reload the recipe before sending.");

        return new StationRecipeDelivery("", row.Id, row.Name, version.Version, version.WorkflowHash,
            RequiresDatasetValidation(row) || await DeliveredVersionRequiresValidationAsync(connection, null, row.Id, version.Version, ct), version.Workflow);
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

        await using var connection = await _db.OpenConnectionAsync(ct, strongDurability: true);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var previous = await ReadReceiptAsync(connection, transaction,
            "WHERE r.station_id=$station AND r.job_id=$job AND r.source_version=$source AND r.workflow_hash=$hash AND r.requires_validation=$required",
            ct, ("$station", delivery.StationId), ("$job", delivery.JobId), ("$source", delivery.SourceVersion),
            ("$hash", hash), ("$required", delivery.RequiresValidation ? 1 : 0));
        if (previous is not null) return previous;

        var row = await LoadJobRowAsync(connection, delivery.JobId, transaction, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is not null && now <= row.UpdatedAt) now = row.UpdatedAt.AddTicks(1);
        var localVersion = row is null ? 1 : checked(row.LatestVersion + 1);
        var name = row?.Name ?? delivery.JobName.Trim();
        var occurredAt = Iso(now);
        if (row is null)
        {
            await ExecuteAsync(connection, transaction, """
INSERT INTO jobs(id,name,description,created_at,updated_at,latest_version,requires_validation)
VALUES($job,$name,'',$at,$at,1,$required);
""", ct, ("$job", delivery.JobId), ("$name", name), ("$at", occurredAt), ("$required", delivery.RequiresValidation ? 1 : 0));
        }
        else
        {
            await ExecuteAsync(connection, transaction, "UPDATE jobs SET latest_version=$version,updated_at=$at WHERE id=$job;",
                ct, ("$job", delivery.JobId), ("$version", localVersion), ("$at", occurredAt));
        }

        var snapshot = new JobVersionSnapshot(delivery.JobId, localVersion, hash, now,
            $"Station delivery from source V{delivery.SourceVersion}", delivery.Workflow);
        await InsertVersionAsync(connection, transaction, snapshot, ct);
        var receiptId = Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction, """
INSERT INTO recipe_delivery_receipts(id,station_id,job_id,source_version,workflow_hash,local_version,requires_validation,received_at)
VALUES($id,$station,$job,$source,$hash,$local,$required,$at);
""", ct, ("$id", receiptId), ("$station", delivery.StationId), ("$job", delivery.JobId),
            ("$source", delivery.SourceVersion), ("$local", localVersion), ("$hash", hash),
            ("$required", delivery.RequiresValidation ? 1 : 0), ("$at", occurredAt));
        await transaction.CommitAsync(ct);

        return new RecipeDeliveryReceipt(receiptId, delivery.StationId, delivery.JobId, name, delivery.SourceVersion, hash,
            localVersion, delivery.RequiresValidation || (row is not null && RequiresDatasetValidation(row)), row?.PublishedVersion, now, now);
    }

    internal async Task<IReadOnlyList<RecipeDeliveryReceipt>> ListRecipeDeliveriesAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = RecipeReceiptSelect + "\nORDER BY r.received_at DESC,r.id DESC LIMIT 30;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RecipeDeliveryReceipt>();
        while (await reader.ReadAsync(ct)) result.Add(ReadReceipt(reader));
        return result;
    }

    internal async Task<RecipeDeliveryReceipt?> GetRecipeDeliveryAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        return await ReadReceiptAsync(connection, null, "WHERE r.id=$id", ct, ("$id", id));
    }

    private static async Task<RecipeDeliveryReceipt?> ReadReceiptAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string filter, CancellationToken ct, params (string Key, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RecipeReceiptSelect + "\n" + filter + ";";
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReceipt(reader) : null;
    }

    private static RecipeDeliveryReceipt ReadReceipt(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
            reader.GetString(5), reader.GetInt32(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ParseTime(reader.GetString(9)), ParseTime(reader.GetString(10)));

    private static bool RequiresDatasetValidation(JobRow row)
        => row.RequiresValidation || !string.IsNullOrWhiteSpace(row.ProductId) || !string.IsNullOrWhiteSpace(row.RecipeCode);

    private static async Task<bool> DeliveredVersionRequiresValidationAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string jobId, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT 1 FROM recipe_delivery_receipts WHERE job_id=$job AND local_version=$version AND requires_validation=1;
""";
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteScalarAsync(ct) is not null;
    }
}
