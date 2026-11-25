using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
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
    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public RecipeParameterStore(IVisionStudioDbFactory dbFactory) => _dbFactory = dbFactory;
    public RecipeParameterStore(SqliteMetadataDatabase db) : this(new VisionStudioDbFactory(db)) { }

    public Task<ParameterValueSet> GetProductAsync(string productId, CancellationToken ct)
        => GetAsync("product", productId, ct);

    public Task<ParameterValueSet> GetRecipeAsync(string jobId, CancellationToken ct)
        => GetAsync("recipe", jobId, ct);

    public Task<ParameterValueSet> ReplaceProductAsync(string productId, ReplaceParameterValuesRequest request, CancellationToken ct)
        => ReplaceProductCoreAsync(productId, request.Values, ct);

    public Task<ParameterValueSet> ReplaceRecipeAsync(string jobId, ReplaceParameterValuesRequest request, CancellationToken ct)
        => ReplaceRecipeCoreAsync(jobId, request.Values, ct);

    public async Task<ResolvedRecipeParameterization> ResolveForJobAsync(
        string jobId,
        WorkflowDefinition baseWorkflow,
        IReadOnlyDictionary<string, string>? bindings,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.Jobs
            .Where(x => x.Id == jobId)
            .Select(x => new { x.ProductId })
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiNotFoundException($"Recipe '{jobId}' does not exist.");
        return await ResolveAsync(db, jobId, job.ProductId, baseWorkflow, bindings, ct);
    }

    public async Task<ResolvedRecipeParameterization> ResolveForCreateAsync(
        string jobId,
        string? productId,
        WorkflowDefinition baseWorkflow,
        IReadOnlyDictionary<string, string>? bindings,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ResolveAsync(db, jobId, productId, baseWorkflow, bindings, ct);
    }

    private async Task<ResolvedRecipeParameterization> ResolveAsync(
        VisionStudioDbContext db,
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
            : await LoadProductValuesAsync(db, productId!, ct);
        var recipeValues = await LoadRecipeValuesAsync(db, jobId, ct);

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

    private async Task<ParameterValueSet> GetAsync(string scope, string ownerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        DateTimeOffset? updatedAt = null;
        if (scope == "product")
        {
            var rows = await db.ProductParameterValues.Where(x => x.ProductId == ownerId).OrderBy(x => x.Key).ToListAsync(ct);
            foreach (var r in rows)
            {
                using var document = JsonDocument.Parse(r.ValueJson);
                values[r.Key] = document.RootElement.Clone();
                if (updatedAt is null || r.UpdatedAt > updatedAt) updatedAt = r.UpdatedAt;
            }
        }
        else
        {
            var rows = await db.RecipeParameterValues.Where(x => x.JobId == ownerId).OrderBy(x => x.Key).ToListAsync(ct);
            foreach (var r in rows)
            {
                using var document = JsonDocument.Parse(r.ValueJson);
                values[r.Key] = document.RootElement.Clone();
                if (updatedAt is null || r.UpdatedAt > updatedAt) updatedAt = r.UpdatedAt;
            }
        }
        return new(scope, ownerId, values, updatedAt);
    }

    private async Task<ParameterValueSet> ReplaceProductCoreAsync(string productId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        ValidateValues(values);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.Products.AnyAsync(x => x.Id == productId, ct))
            throw new ApiNotFoundException($"product '{productId}' does not exist.");
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        await db.ProductParameterValues.Where(x => x.ProductId == productId).ExecuteDeleteAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            db.ProductParameterValues.Add(new ProductParameterValueEntity
            {
                ProductId = productId,
                Key = key,
                ValueJson = value.GetRawText(),
                UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync("product", productId, ct);
    }

    private async Task<ParameterValueSet> ReplaceRecipeCoreAsync(string jobId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        ValidateValues(values);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.Jobs.AnyAsync(x => x.Id == jobId, ct))
            throw new ApiNotFoundException($"recipe '{jobId}' does not exist.");
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        await db.RecipeParameterValues.Where(x => x.JobId == jobId).ExecuteDeleteAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            db.RecipeParameterValues.Add(new RecipeParameterValueEntity
            {
                JobId = jobId,
                Key = key,
                ValueJson = value.GetRawText(),
                UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync("recipe", jobId, ct);
    }

    private static async Task<Dictionary<string, JsonElement>> LoadProductValuesAsync(VisionStudioDbContext db, string productId, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rows = await db.ProductParameterValues.Where(x => x.ProductId == productId).ToListAsync(ct);
        foreach (var r in rows)
        {
            using var document = JsonDocument.Parse(r.ValueJson);
            values[r.Key] = document.RootElement.Clone();
        }
        return values;
    }

    private static async Task<Dictionary<string, JsonElement>> LoadRecipeValuesAsync(VisionStudioDbContext db, string jobId, CancellationToken ct)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rows = await db.RecipeParameterValues.Where(x => x.JobId == jobId).ToListAsync(ct);
        foreach (var r in rows)
        {
            using var document = JsonDocument.Parse(r.ValueJson);
            values[r.Key] = document.RootElement.Clone();
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
