using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record ParameterValueSet(
    string Scope,
    string OwnerId,
    IReadOnlyDictionary<string, JsonElement> Values,
    DateTimeOffset? UpdatedAt = null);

public sealed record ReplaceParameterValuesRequest(Dictionary<string, JsonElement> Values);

public sealed record ResolveRecipeParametersRequest(
    WorkflowDefinition Workflow,
    Dictionary<string, string>? Bindings = null);

public sealed record ResolvedRecipeParameterization(
    WorkflowDefinition BaseWorkflow,
    WorkflowDefinition EffectiveWorkflow,
    string BaseWorkflowHash,
    string EffectiveWorkflowHash,
    IReadOnlyDictionary<string, string> Bindings,
    IReadOnlyDictionary<string, JsonElement> Snapshot)
{
    public int BindingCount => Bindings.Count;
}

/// <summary>
/// Product/Recipe parameter drafts are mutable authoring state. They never hot-edit an immutable Recipe Version.
/// Saving a version resolves the binding map and freezes the exact values into job_versions.parameter_snapshot_json.
/// </summary>
public sealed class RecipeParameterStore
{
    private readonly SqliteMetadataDatabase _db;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public RecipeParameterStore(SqliteMetadataDatabase db) => _db = db;

    public Task<ParameterValueSet> GetProductAsync(string productId, CancellationToken ct)
        => GetAsync("product", productId, "product_parameter_values", "product_id", ct);

    public Task<ParameterValueSet> GetRecipeAsync(string jobId, CancellationToken ct)
        => GetAsync("recipe", jobId, "recipe_parameter_values", "job_id", ct);

    public Task<ParameterValueSet> ReplaceProductAsync(string productId, ReplaceParameterValuesRequest request, CancellationToken ct)
        => ReplaceAsync("product", productId, "products", "product_parameter_values", "product_id", request.Values, ct);

    public Task<ParameterValueSet> ReplaceRecipeAsync(string jobId, ReplaceParameterValuesRequest request, CancellationToken ct)
        => ReplaceAsync("recipe", jobId, "jobs", "recipe_parameter_values", "job_id", request.Values, ct);

    public async Task<ResolvedRecipeParameterization> ResolveForJobAsync(
        string jobId,
        WorkflowDefinition baseWorkflow,
        IReadOnlyDictionary<string, string>? bindings,
        CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var job = connection.CreateCommand();
        job.CommandText = "SELECT product_id FROM jobs WHERE id=$id;";
        job.Parameters.AddWithValue("$id", jobId);
        await using var reader = await job.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new ApiNotFoundException($"Recipe '{jobId}' does not exist.");
        var productId = reader.IsDBNull(0) ? null : reader.GetString(0);
        await reader.DisposeAsync();
        return await ResolveAsync(connection, jobId, productId, baseWorkflow, bindings, ct);
    }

    public async Task<ResolvedRecipeParameterization> ResolveForCreateAsync(
        string jobId,
        string? productId,
        WorkflowDefinition baseWorkflow,
        IReadOnlyDictionary<string, string>? bindings,
        CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        return await ResolveAsync(connection, jobId, productId, baseWorkflow, bindings, ct);
    }

    private async Task<ResolvedRecipeParameterization> ResolveAsync(
        SqliteConnection connection,
        string jobId,
        string? productId,
        WorkflowDefinition baseWorkflow,
        IReadOnlyDictionary<string, string>? bindings,
        CancellationToken ct)
    {
        var normalizedBindings = (bindings ?? new Dictionary<string, string>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key.Trim(), x => x.Value.Trim(), StringComparer.Ordinal);

        if (normalizedBindings.Count == 0)
        {
            var hash = WorkflowFingerprint.Compute(baseWorkflow);
            return new(baseWorkflow, baseWorkflow, hash, hash, normalizedBindings, new Dictionary<string, JsonElement>());
        }

        var productValues = string.IsNullOrWhiteSpace(productId)
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : await LoadValuesAsync(connection, "product_parameter_values", "product_id", productId!, ct);
        var recipeValues = await LoadValuesAsync(connection, "recipe_parameter_values", "job_id", jobId, ct);

        var nodeMap = baseWorkflow.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var replacements = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        var snapshot = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (target, source) in normalizedBindings)
        {
            var dot = target.IndexOf('.');
            if (dot <= 0 || dot == target.Length - 1)
                throw new ApiValidationException($"Binding target '{target}' must use '<nodeId>.<parameterName>'.");
            var nodeId = target[..dot];
            var parameterName = target[(dot + 1)..];
            if (parameterName.StartsWith("__", StringComparison.Ordinal) || parameterName is "moduleId" or "moduleVersion" or "moduleHash")
                throw new ApiValidationException($"Binding target '{target}' is reserved module/runtime metadata and cannot be parameterized.");
            if (!nodeMap.TryGetValue(nodeId, out var node))
                throw new ApiValidationException($"Binding target node '{nodeId}' does not exist.");
            if (node.Parameters is null || !node.Parameters.TryGetValue(parameterName, out var baseValue))
                throw new ApiValidationException($"Binding target '{target}' is not an existing node parameter.");

            var sourceDot = source.IndexOf('.');
            if (sourceDot <= 0 || sourceDot == source.Length - 1)
                throw new ApiValidationException($"Binding source '{source}' must use 'product.<key>' or 'recipe.<key>'.");
            var scope = source[..sourceDot].ToLowerInvariant();
            var key = source[(sourceDot + 1)..];
            var values = scope switch
            {
                "product" when !string.IsNullOrWhiteSpace(productId) => productValues,
                "product" => throw new ApiValidationException($"Binding '{target}' references product.{key}, but Recipe '{jobId}' is not assigned to a Product."),
                "recipe" => recipeValues,
                _ => throw new ApiValidationException($"Binding source '{source}' must start with 'product.' or 'recipe.'.")
            };
            if (!values.TryGetValue(key, out var value))
                throw new ApiValidationException($"Binding source '{source}' has no current draft value.");
            if (!IsCompatible(baseValue.ValueKind, value.ValueKind))
                throw new ApiValidationException($"Binding '{target}' expects {baseValue.ValueKind}, but '{source}' is {value.ValueKind}.");

            if (!replacements.TryGetValue(nodeId, out var nodeReplacements))
                replacements[nodeId] = nodeReplacements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            nodeReplacements[parameterName] = value.Clone();
            snapshot[source] = value.Clone();
        }

        var resolvedNodes = baseWorkflow.Nodes.Select(node =>
        {
            if (!replacements.TryGetValue(node.Id, out var values)) return node;
            var parameters = node.Parameters is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : node.Parameters.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
            foreach (var (key, value) in values) parameters[key] = value.Clone();
            return node with { Parameters = parameters };
        }).ToArray();

        var effective = baseWorkflow with { Nodes = resolvedNodes };
        return new(
            baseWorkflow,
            effective,
            WorkflowFingerprint.Compute(baseWorkflow),
            WorkflowFingerprint.Compute(effective),
            normalizedBindings,
            snapshot);
    }

    private async Task<ParameterValueSet> GetAsync(string scope, string ownerId, string table, string ownerColumn, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        DateTimeOffset? updatedAt = null;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT key,value_json,updated_at FROM {table} WHERE {ownerColumn}=$id ORDER BY key;";
        command.Parameters.AddWithValue("$id", ownerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            values[reader.GetString(0)] = document.RootElement.Clone();
            var at = DateTimeOffset.Parse(reader.GetString(2));
            if (updatedAt is null || at > updatedAt) updatedAt = at;
        }
        return new(scope, ownerId, values, updatedAt);
    }

    private async Task<ParameterValueSet> ReplaceAsync(
        string scope,
        string ownerId,
        string ownerTable,
        string table,
        string ownerColumn,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken ct)
    {
        ValidateValues(values);
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"SELECT 1 FROM {ownerTable} WHERE id=$id;";
            exists.Parameters.AddWithValue("$id", ownerId);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new ApiNotFoundException($"{scope} '{ownerId}' does not exist.");
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE {ownerColumn}=$id;";
            delete.Parameters.AddWithValue("$id", ownerId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {table}({ownerColumn},key,value_json,updated_at) VALUES($id,$key,$value,$updated);";
            insert.Parameters.AddWithValue("$id", ownerId);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value.GetRawText());
            insert.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await GetAsync(scope, ownerId, table, ownerColumn, ct);
    }

    private static async Task<Dictionary<string, JsonElement>> LoadValuesAsync(SqliteConnection connection, string table, string ownerColumn, string ownerId, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT key,value_json FROM {table} WHERE {ownerColumn}=$id;";
        command.Parameters.AddWithValue("$id", ownerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            values[reader.GetString(0)] = document.RootElement.Clone();
        }
        return values;
    }


    private static bool IsCompatible(JsonValueKind expected, JsonValueKind actual)
    {
        if (expected == actual) return true;
        if (actual == JsonValueKind.Null) return expected is JsonValueKind.Null;
        return false;
    }

    private static void ValidateValues(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count > 256) throw new ApiValidationException("A parameter scope can contain at most 256 values.");
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
                throw new ApiValidationException($"Invalid parameter key '{key}'. Use letters, digits, '_', '-' or '.'.");
            if (value.ValueKind is JsonValueKind.Undefined)
                throw new ApiValidationException($"Parameter '{key}' has an undefined JSON value.");
        }
    }
}
