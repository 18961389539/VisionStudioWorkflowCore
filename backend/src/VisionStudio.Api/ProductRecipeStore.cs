using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
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
public sealed record CloneRecipeRequest(string NewId, string ProductId, string RecipeCode, string Name, string? Description = null, int? SourceVersion = null);

public sealed record ValidationAcceptancePolicy(
    double MinimumAccuracy = 0.99,
    double MaximumFalseOkRate = 0,
    double MaximumFalseNgRate = 0.01,
    int MaximumErrors = 0);

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

public sealed class ProductRecipeStore(SqliteMetadataDatabase db, JobStore jobs)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ProductDescriptor>> ListProductsAsync(CancellationToken ct)
    {
        var products = new List<ProductDescriptor>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,description,created_at,updated_at FROM products ORDER BY updated_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(string Id,string Name,string Description,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt)>();
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4))));
        await reader.DisposeAsync();
        foreach (var row in rows)
            products.Add(new ProductDescriptor(row.Id,row.Name,row.Description,row.CreatedAt,row.UpdatedAt,await ListRecipesAsync(connection,row.Id,ct)));
        return products;
    }

    public async Task<ProductDescriptor> CreateProductAsync(CreateProductRequest request, CancellationToken ct)
    {
        ValidateId(request.Id, "Product");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Product name is required.");
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO products(id,name,description,created_at,updated_at) VALUES($id,$name,$description,$created,$updated);";
            command.Parameters.AddWithValue("$id", request.Id);
            command.Parameters.AddWithValue("$name", request.Name.Trim());
            command.Parameters.AddWithValue("$description", request.Description?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$created", Iso(now));
            command.Parameters.AddWithValue("$updated", Iso(now));
            await command.ExecuteNonQueryAsync(ct);
            return (await ListProductsAsync(ct)).Single(x => x.Id == request.Id);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Product '{request.Id}' already exists.", ex);
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
        await EnsureProductAsync(request.ProductId, ct);
        ValidateRecipeCode(request.RecipeCode);
        var source = await jobs.GetAsync(sourceJobId, ct) ?? throw new ApiNotFoundException($"Recipe '{sourceJobId}' does not exist.");
        var version = request.SourceVersion ?? source.PublishedVersion ?? source.LatestVersion;
        var snapshot = await jobs.GetVersionAsync(sourceJobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{sourceJobId}' V{version} does not exist.");
        var result = await jobs.CreateAsync(new CreateJobRequest(
            request.NewId, request.Name, request.Description ?? source.Description, snapshot.Workflow,
            $"Cloned from {sourceJobId} V{version}", request.ProductId, request.RecipeCode.Trim()), ct);
        await TouchProductAsync(request.ProductId, ct);
        return result;
    }

    public async Task<IReadOnlyList<ValidationCandidateInfo>> ValidationCandidatesAsync(string jobId, int version, CancellationToken ct)
    {
        var snapshot = await jobs.GetVersionAsync(jobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{version} does not exist.");
        var result = new List<ValidationCandidateInfo>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id,dataset_id,started_at,completed_at,summary_json
FROM validation_runs
WHERE status='Completed' AND workflow_hash=$hash AND summary_json IS NOT NULL
ORDER BY completed_at DESC, started_at DESC LIMIT 50;
""";
        command.Parameters.AddWithValue("$hash", snapshot.WorkflowHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var summary = JsonSerializer.Deserialize<ValidationRunSummary>(reader.GetString(4), _json);
            if (summary is not null)
                result.Add(new ValidationCandidateInfo(reader.GetString(0),reader.GetString(1),DateTimeOffset.Parse(reader.GetString(2)),reader.IsDBNull(3)?null:DateTimeOffset.Parse(reader.GetString(3)),summary));
        }
        return result;
    }

    public async Task<JobVersionValidationInfo> LinkValidationAsync(string jobId, int version, LinkRecipeValidationRequest request, CancellationToken ct)
    {
        var snapshot = await jobs.GetVersionAsync(jobId, version, ct) ?? throw new ApiNotFoundException($"Recipe '{jobId}' V{version} does not exist.");
        var policy = request.Policy ?? new ValidationAcceptancePolicy();
        ValidatePolicy(policy);
        await using var connection = await db.OpenConnectionAsync(ct);
        string datasetId, hash, status, summaryJson;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT dataset_id,workflow_hash,status,summary_json FROM validation_runs WHERE run_id=$run;";
            command.Parameters.AddWithValue("$run", request.ValidationRunId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ApiNotFoundException($"Validation run '{request.ValidationRunId}' does not exist.");
            datasetId=reader.GetString(0); hash=reader.GetString(1); status=reader.GetString(2); summaryJson=reader.IsDBNull(3)?string.Empty:reader.GetString(3);
        }
        if (!string.Equals(status,"Completed",StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException("Only completed Validation runs can approve a Recipe version.");
        if (!string.Equals(hash,snapshot.WorkflowHash,StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException("Validation workflow hash does not match the immutable Recipe version.");
        var summary = string.IsNullOrWhiteSpace(summaryJson) ? null : JsonSerializer.Deserialize<ValidationRunSummary>(summaryJson,_json);
        if (summary is null) throw new ApiConflictException("Validation run has no completed summary.");
        var reasons = Evaluate(summary,policy);
        var accepted = reasons.Count == 0;
        var reason = accepted ? "Accepted" : string.Join("; ", reasons);
        var linkedAt=DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO job_version_validations(job_id,version,validation_run_id,dataset_id,workflow_hash,accepted,policy_json,summary_json,reason,linked_at)
VALUES($job,$version,$run,$dataset,$hash,$accepted,$policy,$summary,$reason,$linked)
ON CONFLICT(job_id,version) DO UPDATE SET validation_run_id=excluded.validation_run_id,dataset_id=excluded.dataset_id,workflow_hash=excluded.workflow_hash,accepted=excluded.accepted,policy_json=excluded.policy_json,summary_json=excluded.summary_json,reason=excluded.reason,linked_at=excluded.linked_at;
""";
            command.Parameters.AddWithValue("$job",jobId); command.Parameters.AddWithValue("$version",version); command.Parameters.AddWithValue("$run",request.ValidationRunId);
            command.Parameters.AddWithValue("$dataset",datasetId); command.Parameters.AddWithValue("$hash",hash); command.Parameters.AddWithValue("$accepted",accepted?1:0);
            command.Parameters.AddWithValue("$policy",JsonSerializer.Serialize(policy,_json)); command.Parameters.AddWithValue("$summary",summaryJson); command.Parameters.AddWithValue("$reason",reason); command.Parameters.AddWithValue("$linked",Iso(linkedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        return new JobVersionValidationInfo(request.ValidationRunId,datasetId,accepted,linkedAt,reason,summary,policy);
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

    private static List<string> Evaluate(ValidationRunSummary s, ValidationAcceptancePolicy p)
    {
        var reasons=new List<string>();
        if(s.Accuracy < p.MinimumAccuracy) reasons.Add($"Accuracy {s.Accuracy:P2} < {p.MinimumAccuracy:P2}");
        if(s.FalseOkRate > p.MaximumFalseOkRate) reasons.Add($"False OK {s.FalseOkRate:P2} > {p.MaximumFalseOkRate:P2}");
        if(s.FalseNgRate > p.MaximumFalseNgRate) reasons.Add($"False NG {s.FalseNgRate:P2} > {p.MaximumFalseNgRate:P2}");
        if(s.Errors > p.MaximumErrors) reasons.Add($"Errors {s.Errors} > {p.MaximumErrors}");
        if(s.Completed == 0) reasons.Add("Validation contains no completed samples");
        return reasons;
    }

    private async Task<IReadOnlyList<ProductRecipeSummary>> ListRecipesAsync(SqliteConnection connection,string productId,CancellationToken ct)
    {
        var result=new List<ProductRecipeSummary>();
        await using var command=connection.CreateCommand();
        command.CommandText="""
SELECT j.id,j.recipe_code,j.name,j.description,j.latest_version,j.published_version,j.updated_at,
       (SELECT accepted FROM job_version_validations v WHERE v.job_id=j.id AND v.version=j.latest_version)
FROM jobs j WHERE j.product_id=$product ORDER BY j.recipe_code,j.name;
""";
        command.Parameters.AddWithValue("$product",productId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var published=reader.IsDBNull(5)?(int?)null:reader.GetInt32(5); var accepted=!reader.IsDBNull(7)&&reader.GetInt32(7)!=0;
            var state=published==reader.GetInt32(4)?"Published":accepted?"Validated":"Draft";
            result.Add(new(reader.GetString(0),reader.IsDBNull(1)?reader.GetString(0):reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),published,state,DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    private async Task EnsureProductAsync(string id,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct); await using var command=connection.CreateCommand();
        command.CommandText="SELECT 1 FROM products WHERE id=$id;"; command.Parameters.AddWithValue("$id",id);
        if(await command.ExecuteScalarAsync(ct) is null) throw new ApiNotFoundException($"Product '{id}' does not exist.");
    }
    private async Task TouchProductAsync(string id,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct); await using var command=connection.CreateCommand();
        command.CommandText="UPDATE products SET updated_at=$at WHERE id=$id;"; command.Parameters.AddWithValue("$at",Iso(DateTimeOffset.UtcNow)); command.Parameters.AddWithValue("$id",id); await command.ExecuteNonQueryAsync(ct);
    }
    private static void ValidatePolicy(ValidationAcceptancePolicy p)
    {
        if(p.MinimumAccuracy is <0 or >1 || p.MaximumFalseOkRate is <0 or >1 || p.MaximumFalseNgRate is <0 or >1 || p.MaximumErrors<0)
            throw new ApiValidationException("Validation policy rates must be 0..1 and MaximumErrors must be >= 0.");
    }
    private static void ValidateRecipeCode(string code){ if(string.IsNullOrWhiteSpace(code)||code.Length>64) throw new ApiValidationException("Recipe code is required and must be <= 64 characters."); }
    private static void ValidateId(string id,string kind){ if(string.IsNullOrWhiteSpace(id)||id.Any(c=>!(char.IsLetterOrDigit(c)||c is '-' or '_'))) throw new ApiValidationException($"{kind} id may contain only letters, digits, '-' and '_'."); }
    private static string Iso(DateTimeOffset v)=>v.ToUniversalTime().ToString("O");
}
