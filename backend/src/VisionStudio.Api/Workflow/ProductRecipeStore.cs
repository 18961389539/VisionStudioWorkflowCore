using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record ProductRecipeSummary(
    string Id,
    string RecipeCode,
    string Name,
    string Description,
    int LatestVersion,
    int? ActiveVersion,
    string LifecycleState,
    DateTimeOffset UpdatedAt);

public sealed record ProductDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductRecipeSummary> Recipes);

public sealed record CreateProductRequest(string Id, string Name, string? Description = null);
public sealed record CreateProductRecipeRequest(string Id, string RecipeCode, string Name, string? Description, WorkflowDefinition Workflow, string? ChangeNote = null, Dictionary<string,string>? ParameterBindings = null);
public sealed record CloneRecipeRequest(string NewId, string? ProductId, string? RecipeCode, string Name, string? Description = null, int? SourceVersion = null);

public sealed record ValidationAcceptancePolicy(
    double MinimumAccuracy = 0.99,
    double MaximumFalseOkRate = 0,
    double MaximumFalseNgRate = 0.01,
    int MaximumErrors = 0,
    int MinimumSamplesPerDisposition = 5);

public sealed record LinkRecipeValidationRequest(string ValidationRunId, ValidationAcceptancePolicy? Policy = null);

public sealed record JobVersionValidationInfo(
    string ValidationRunId,
    string DatasetId,
    bool Accepted,
    DateTimeOffset LinkedAt,
    string Reason,
    ValidationRunSummary? Summary,
    ValidationAcceptancePolicy? Policy);

public sealed record ValidationCandidateInfo(
    string ValidationRunId,
    string DatasetId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    ValidationRunSummary Summary);

public sealed record WorkflowChange(string Kind, string Path, string? Before, string? After);
public sealed record JobVersionDiff(string JobId, int FromVersion, int ToVersion, string FromHash, string ToHash, IReadOnlyList<WorkflowChange> Changes)
{
    public bool Changed => Changes.Count > 0;
}

public sealed class ProductRecipeStore(IVisionStudioDbFactory dbFactory, JobStore jobs)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ProductRecipeStore(SqliteMetadataDatabase db, JobStore jobs)
        : this(new VisionStudioDbFactory(db), jobs) { }

    public async Task<IReadOnlyList<ProductDescriptor>> ListProductsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Products.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var products = new List<ProductDescriptor>(rows.Count);
        foreach (var row in rows)
            products.Add(new ProductDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, await ListRecipesAsync(db, row.Id, ct)));
        return products;
    }

    public async Task<ProductDescriptor> CreateProductAsync(CreateProductRequest request, CancellationToken ct)
    {
        ValidateId(request.Id, "Product");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Product name is required.");
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Products.Add(new ProductEntity
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            return (await ListProductsAsync(ct)).Single(x => x.Id == request.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Product '{request.Id}' already exists.", sqliteEx);
        }
    }

    public async Task<JobDescriptor> CreateRecipeAsync(string productId, CreateProductRecipeRequest request, CancellationToken ct)
    {
        await EnsureProductAsync(productId, ct);
        ValidateRecipeCode(request.RecipeCode);
        var created = await jobs.CreateAsync(new CreateJobRequest(
            request.Id, request.Name, request.Description, request.Workflow,
            request.ChangeNote ?? "Initial recipe version", productId, request.RecipeCode.Trim(), request.ParameterBindings), ct);
        await TouchProductAsync(productId, ct);
        return created;
    }

    public async Task<JobDescriptor> CloneRecipeAsync(string sourceJobId, CloneRecipeRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ProductId)) await EnsureProductAsync(request.ProductId, ct);
        if (request.RecipeCode?.Trim().Length > 64) throw new ApiValidationException("Recipe code must be at most 64 characters.");
        var source = await jobs.GetAsync(sourceJobId, ct) ?? throw new ApiNotFoundException($"Recipe '{sourceJobId}' does not exist.");
        var version = request.SourceVersion ?? source.PublishedVersion ?? source.LatestVersion;
        var snapshot = await jobs.GetVersionAsync(sourceJobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{sourceJobId}' V{version} does not exist.");
        var result = await jobs.CreateAsync(new CreateJobRequest(
            request.NewId, request.Name, request.Description ?? source.Description, snapshot.Workflow,
            $"Cloned from {sourceJobId} V{version}", request.ProductId, request.RecipeCode?.Trim(), RequiresValidation: true), ct);
        if (!string.IsNullOrWhiteSpace(request.ProductId)) await TouchProductAsync(request.ProductId, ct);
        return result;
    }

    public async Task<IReadOnlyList<ValidationCandidateInfo>> ValidationCandidatesAsync(string jobId, int version, CancellationToken ct)
    {
        var snapshot = await jobs.GetVersionAsync(jobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{version} does not exist.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ValidationRuns
            .Where(x => x.Status == "Completed" && x.WorkflowHash == snapshot.WorkflowHash && x.SummaryJson != null)
            .OrderByDescending(x => x.CompletedAt)
            .ThenByDescending(x => x.StartedAt)
            .Take(50)
            .ToListAsync(ct);
        var result = new List<ValidationCandidateInfo>(rows.Count);
        foreach (var r in rows)
        {
            var summary = JsonSerializer.Deserialize<ValidationRunSummary>(r.SummaryJson!, _json);
            if (summary is not null)
                result.Add(new ValidationCandidateInfo(r.RunId, r.DatasetId, r.StartedAt, r.CompletedAt, summary));
        }
        return result;
    }

    public async Task<JobVersionValidationInfo> LinkValidationAsync(string jobId, int version, LinkRecipeValidationRequest request, CancellationToken ct)
    {
        var snapshot = await jobs.GetVersionAsync(jobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{version} does not exist.");
        var policy = request.Policy ?? new ValidationAcceptancePolicy();
        ValidatePolicy(policy);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var validationRun = await db.ValidationRuns
            .Where(x => x.RunId == request.ValidationRunId)
            .Select(x => new { x.DatasetId, x.WorkflowHash, x.Status, x.SummaryJson })
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiNotFoundException($"Validation run '{request.ValidationRunId}' does not exist.");

        if (!string.Equals(validationRun.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("Only completed Validation runs can approve a Recipe version.");
        if (!string.Equals(validationRun.WorkflowHash, snapshot.WorkflowHash, StringComparison.OrdinalIgnoreCase))
            throw new ApiConflictException("Validation workflow hash does not match the immutable Recipe version.");
        var summaryJson = validationRun.SummaryJson ?? string.Empty;
        var summary = string.IsNullOrWhiteSpace(summaryJson) ? null : JsonSerializer.Deserialize<ValidationRunSummary>(summaryJson, _json);
        if (summary is null) throw new ApiConflictException("Validation run has no completed summary.");
        var reasons = Evaluate(summary, policy);
        var accepted = reasons.Count == 0;
        var reason = accepted ? "Accepted" : string.Join("; ", reasons);
        var linkedAt = DateTimeOffset.UtcNow;
        var policyJson = JsonSerializer.Serialize(policy, _json);

        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        var entity = await db.JobVersionValidations.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId && x.Version == version, ct);
        if (entity is null)
        {
            db.JobVersionValidations.Add(new JobVersionValidationEntity
            {
                JobId = jobId,
                Version = version,
                ValidationRunId = request.ValidationRunId,
                DatasetId = validationRun.DatasetId,
                WorkflowHash = validationRun.WorkflowHash,
                Accepted = accepted,
                PolicyJson = policyJson,
                SummaryJson = summaryJson,
                Reason = reason,
                LinkedAt = linkedAt
            });
        }
        else
        {
            entity.ValidationRunId = request.ValidationRunId;
            entity.DatasetId = validationRun.DatasetId;
            entity.WorkflowHash = validationRun.WorkflowHash;
            entity.Accepted = accepted;
            entity.PolicyJson = policyJson;
            entity.SummaryJson = summaryJson;
            entity.Reason = reason;
            entity.LinkedAt = linkedAt;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new JobVersionValidationInfo(request.ValidationRunId, validationRun.DatasetId, accepted, linkedAt, reason, summary, policy);
    }

    public async Task<JobVersionDiff> DiffAsync(string jobId, int fromVersion, int toVersion, CancellationToken ct)
    {
        var before = await jobs.GetVersionAsync(jobId,fromVersion,ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{fromVersion} does not exist.");
        var after = await jobs.GetVersionAsync(jobId,toVersion,ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{toVersion} does not exist.");
        var changes = Diff(before.Workflow,after.Workflow);
        return new JobVersionDiff(jobId,fromVersion,toVersion,before.WorkflowHash,after.WorkflowHash,changes);
    }

    private static IReadOnlyList<WorkflowChange> Diff(WorkflowDefinition before, WorkflowDefinition after)
    {
        var changes=new List<WorkflowChange>();
        var a=before.Nodes.ToDictionary(x=>x.Id,StringComparer.Ordinal); var b=after.Nodes.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        foreach(var id in a.Keys.Except(b.Keys).Order()) changes.Add(new("NodeRemoved",$"nodes/{id}",a[id].Type,null));
        foreach(var id in b.Keys.Except(a.Keys).Order()) changes.Add(new("NodeAdded",$"nodes/{id}",null,b[id].Type));
        foreach(var id in a.Keys.Intersect(b.Keys).Order())
        {
            if(a[id].Type!=b[id].Type) changes.Add(new("NodeType",$"nodes/{id}/type",a[id].Type,b[id].Type));
            var ap=a[id].Parameters ?? new Dictionary<string, JsonElement>(); var bp=b[id].Parameters ?? new Dictionary<string, JsonElement>();
            foreach(var key in ap.Keys.Union(bp.Keys).Order())
            {
                var av=ap.TryGetValue(key,out var ae)?ae.GetRawText():null; var bv=bp.TryGetValue(key,out var be)?be.GetRawText():null;
                if(!string.Equals(av,bv,StringComparison.Ordinal)) changes.Add(new("Parameter",$"nodes/{id}/parameters/{key}",av,bv));
            }
        }
        static string EdgeKey(EdgeDefinition e)=>$"{e.SourceNodeId}:{e.SourcePort}->{e.TargetNodeId}:{e.TargetPort}:{e.Kind}";
        var beforeEdgeKeys=before.Edges.Select(EdgeKey).ToHashSet(StringComparer.Ordinal); var afterEdgeKeys=after.Edges.Select(EdgeKey).ToHashSet(StringComparer.Ordinal);
        foreach(var x in beforeEdgeKeys.Except(afterEdgeKeys).Order()) changes.Add(new("EdgeRemoved","edges",x,null));
        foreach(var x in afterEdgeKeys.Except(beforeEdgeKeys).Order()) changes.Add(new("EdgeAdded","edges",null,x));
        return changes;
    }

    public static List<string> Evaluate(ValidationRunSummary s, ValidationAcceptancePolicy p)
    {
        var reasons=new List<string>();
        if(s.Accuracy < p.MinimumAccuracy) reasons.Add($"Accuracy {s.Accuracy:P2} < {p.MinimumAccuracy:P2}");
        var expectedNg = s.TrueNg + s.FalseOk;
        var expectedOk = s.TrueOk + s.FalseNg;
        if (expectedNg < p.MinimumSamplesPerDisposition)
            reasons.Add($"Validation has {expectedNg} expected NG samples; at least {p.MinimumSamplesPerDisposition} are required to evaluate false-OK rate");
        else if (s.FalseOkRate is not { } falseOkRate)
            reasons.Add("False-OK rate is not evaluable");
        else if(falseOkRate > p.MaximumFalseOkRate)
            reasons.Add($"False OK {falseOkRate:P2} > {p.MaximumFalseOkRate:P2}");
        if (expectedOk < p.MinimumSamplesPerDisposition)
            reasons.Add($"Validation has {expectedOk} expected OK samples; at least {p.MinimumSamplesPerDisposition} are required to evaluate false-NG rate");
        else if (s.FalseNgRate is not { } falseNgRate)
            reasons.Add("False-NG rate is not evaluable");
        else if(falseNgRate > p.MaximumFalseNgRate)
            reasons.Add($"False NG {falseNgRate:P2} > {p.MaximumFalseNgRate:P2}");
        if(s.Errors > p.MaximumErrors) reasons.Add($"Errors {s.Errors} > {p.MaximumErrors}");
        if(s.Completed == 0) reasons.Add("Validation contains no completed samples");
        return reasons;
    }

    private async Task<IReadOnlyList<ProductRecipeSummary>> ListRecipesAsync(VisionStudioDbContext db, string productId, CancellationToken ct)
    {
        var rows = await (
            from j in db.Jobs
            where j.ProductId == productId
            join v in db.JobVersionValidations on new { JobId = j.Id, Version = j.LatestVersion } equals new { v.JobId, v.Version } into vg
            from v in vg.DefaultIfEmpty()
            orderby j.RecipeCode, j.Name
            select new
            {
                j.Id,
                j.RecipeCode,
                j.Name,
                j.Description,
                j.LatestVersion,
                j.PublishedVersion,
                j.UpdatedAt,
                Accepted = v != null && v.Accepted
            })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var state = r.PublishedVersion == r.LatestVersion ? "Published" : r.Accepted ? "Validated" : "Draft";
            return new ProductRecipeSummary(r.Id, r.RecipeCode ?? r.Id, r.Name, r.Description, r.LatestVersion, r.PublishedVersion, state, r.UpdatedAt);
        }).ToList();
    }

    private async Task EnsureProductAsync(string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Products.AnyAsync(x => x.Id == id, ct))
            throw new ApiNotFoundException($"Product '{id}' does not exist.");
    }

    private async Task TouchProductAsync(string id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Products.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now), ct);
    }
    private static void ValidatePolicy(ValidationAcceptancePolicy p)
    {
        if(p.MinimumAccuracy is <0 or >1 || p.MaximumFalseOkRate is <0 or >1 || p.MaximumFalseNgRate is <0 or >1 ||
           p.MaximumErrors < 0 || p.MinimumSamplesPerDisposition is < 1 or > 100_000)
            throw new ApiValidationException("Validation policy rates must be 0..1, MaximumErrors must be >= 0, and MinimumSamplesPerDisposition must be 1..100000.");
    }
    private static void ValidateRecipeCode(string code){ if(string.IsNullOrWhiteSpace(code)||code.Length>64) throw new ApiValidationException("Recipe code is required and must be <= 64 characters."); }
    private static void ValidateId(string id,string kind){ if(string.IsNullOrWhiteSpace(id)||id.Any(c=>!(char.IsLetterOrDigit(c)||c is '-' or '_'))) throw new ApiValidationException($"{kind} id may contain only letters, digits, '-' and '_'."); }
    private static string Iso(DateTimeOffset v)=>v.ToUniversalTime().ToString("O");
}
