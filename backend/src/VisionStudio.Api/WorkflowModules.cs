using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record WorkflowModulePort(
    string Name,
    VisionDataType DataType,
    bool Required,
    string InternalNodeId,
    string InternalPort);

public sealed record WorkflowModuleParameter(
    string Name,
    string Label,
    string Type,
    JsonElement DefaultValue,
    string InternalNodeId,
    string InternalParameter,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<ParameterOption>? Options = null,
    string? Unit = null,
    string? Group = null,
    string? Description = null);

public sealed record WorkflowModuleVersion(
    string ModuleId,
    int Version,
    string ModuleHash,
    DateTimeOffset CreatedAt,
    string Note,
    WorkflowDefinition Workflow,
    IReadOnlyList<WorkflowModulePort> Inputs,
    IReadOnlyList<WorkflowModulePort> Outputs,
    IReadOnlyList<WorkflowModuleParameter> Parameters);

public sealed record WorkflowModuleDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    IReadOnlyList<WorkflowModuleVersion> Versions);

public sealed record ExtractWorkflowModuleRequest(
    WorkflowDefinition Workflow,
    IReadOnlyList<string> SelectedNodeIds,
    string Id,
    string Name,
    string? Description = null,
    string? Note = null);

public sealed record ExtractWorkflowModuleResult(
    WorkflowModuleDescriptor Module,
    WorkflowModuleVersion Version,
    WorkflowDefinition ReplacementWorkflow);

public sealed record SaveWorkflowModuleVersionRequest(
    WorkflowDefinition Workflow,
    string? Note = null);

public sealed record ExpandWorkflowModulesRequest(WorkflowDefinition Workflow);
public sealed record WorkflowModuleDependency(string InstanceNodeId, string ModuleId, int Version, string ModuleHash);
public sealed record ExpandedWorkflowModules(WorkflowDefinition Workflow, IReadOnlyList<WorkflowModuleDependency> Dependencies);

public sealed class WorkflowModuleStore(SqliteMetadataDatabase db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkflowModuleDescriptor>> ListAsync(CancellationToken ct)
    {
        var modules = new List<(string Id,string Name,string Description,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,int LatestVersion)>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version FROM workflow_modules ORDER BY updated_at DESC,id;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                modules.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),DateTimeOffset.Parse(reader.GetString(3)),DateTimeOffset.Parse(reader.GetString(4)),reader.GetInt32(5)));
        }

        var result = new List<WorkflowModuleDescriptor>(modules.Count);
        foreach (var module in modules)
            result.Add(new WorkflowModuleDescriptor(module.Id,module.Name,module.Description,module.CreatedAt,module.UpdatedAt,module.LatestVersion,await ListVersionsAsync(connection,module.Id,ct)));
        return result;
    }

    public async Task<WorkflowModuleDescriptor?> GetAsync(string id, CancellationToken ct)
        => (await ListAsync(ct)).FirstOrDefault(x => x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));

    public async Task<WorkflowModuleVersion?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT module_hash,created_at,note,workflow_json,inputs_json,outputs_json,parameters_json FROM workflow_module_versions WHERE module_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id",id);
        command.Parameters.AddWithValue("$version",version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadVersion(id,version,reader);
    }

    public async Task<WorkflowModuleDescriptor> CreateAsync(
        string id,
        string name,
        string description,
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowModulePort> inputs,
        IReadOnlyList<WorkflowModulePort> outputs,
        IReadOnlyList<WorkflowModuleParameter> parameters,
        string note,
        CancellationToken ct)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException("Module name is required.");
        var now = DateTimeOffset.UtcNow;
        var hash = ComputeHash(workflow,inputs,outputs,parameters);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred:false);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction=tx;
                command.CommandText="INSERT INTO workflow_modules(id,name,description,created_at,updated_at,latest_version) VALUES($id,$name,$description,$at,$at,1);";
                command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$name",name.Trim()); command.Parameters.AddWithValue("$description",description.Trim()); command.Parameters.AddWithValue("$at",Iso(now));
                await command.ExecuteNonQueryAsync(ct);
            }
            await InsertVersionAsync(connection,tx,id,1,hash,now,note,workflow,inputs,outputs,parameters,ct);
            await tx.CommitAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode==19)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new ApiConflictException($"Workflow module '{id}' already exists.",ex);
        }
        return (await GetAsync(id,ct))!;
    }

    public async Task<WorkflowModuleVersion> AddVersionAsync(string id, WorkflowDefinition workflow, string note, CancellationToken ct)
    {
        var module = await GetAsync(id,ct) ?? throw new ApiNotFoundException($"Workflow module '{id}' does not exist.");
        var previous = module.Versions.OrderByDescending(x=>x.Version).First();
        var inputs = previous.Inputs;
        var outputs = previous.Outputs;
        var parameters = RebaseParameterDefaults(previous.Parameters,workflow);
        ValidateInterface(workflow,inputs,outputs,parameters);
        var version=module.LatestVersion+1;
        var now=DateTimeOffset.UtcNow;
        var hash=ComputeHash(workflow,inputs,outputs,parameters);
        await using var connection=await db.OpenConnectionAsync(ct);
        await using var tx=connection.BeginTransaction(deferred:false);
        await InsertVersionAsync(connection,tx,id,version,hash,now,note,workflow,inputs,outputs,parameters,ct);
        await using (var update=connection.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="UPDATE workflow_modules SET latest_version=$version,updated_at=$at WHERE id=$id;";
            update.Parameters.AddWithValue("$version",version); update.Parameters.AddWithValue("$at",Iso(now)); update.Parameters.AddWithValue("$id",id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await GetVersionAsync(id,version,ct))!;
    }

    private static IReadOnlyList<WorkflowModuleParameter> RebaseParameterDefaults(IReadOnlyList<WorkflowModuleParameter> existing, WorkflowDefinition workflow)
    {
        var nodes=workflow.Nodes.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);
        return existing.Select(parameter =>
        {
            if (!nodes.TryGetValue(parameter.InternalNodeId,out var node) || node.Parameters is null || !node.Parameters.TryGetValue(parameter.InternalParameter,out var value))
                throw new ApiValidationException($"Module parameter '{parameter.Name}' target '{parameter.InternalNodeId}.{parameter.InternalParameter}' no longer exists.");
            return parameter with { DefaultValue=value.Clone() };
        }).ToArray();
    }

    public static void ValidateInterface(
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowModulePort> inputs,
        IReadOnlyList<WorkflowModulePort> outputs,
        IReadOnlyList<WorkflowModuleParameter> parameters)
    {
        var nodes=workflow.Nodes.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);
        foreach (var port in inputs.Concat(outputs))
            if (!nodes.ContainsKey(port.InternalNodeId)) throw new ApiValidationException($"Module interface '{port.Name}' references missing node '{port.InternalNodeId}'.");
        foreach (var parameter in parameters)
        {
            if (!nodes.TryGetValue(parameter.InternalNodeId,out var node) || node.Parameters is null || !node.Parameters.ContainsKey(parameter.InternalParameter))
                throw new ApiValidationException($"Module parameter '{parameter.Name}' references missing parameter '{parameter.InternalNodeId}.{parameter.InternalParameter}'.");
        }
        if (inputs.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=inputs.Count) throw new ApiValidationException("Module input names must be unique.");
        if (outputs.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=outputs.Count) throw new ApiValidationException("Module output names must be unique.");
        if (parameters.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=parameters.Count) throw new ApiValidationException("Module parameter names must be unique.");
    }

    private static async Task InsertVersionAsync(SqliteConnection connection,SqliteTransaction tx,string id,int version,string hash,DateTimeOffset at,string note,WorkflowDefinition workflow,IReadOnlyList<WorkflowModulePort> inputs,IReadOnlyList<WorkflowModulePort> outputs,IReadOnlyList<WorkflowModuleParameter> parameters,CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.Transaction=tx;
        command.CommandText="""
INSERT INTO workflow_module_versions(module_id,version,module_hash,created_at,note,workflow_json,inputs_json,outputs_json,parameters_json)
VALUES($id,$version,$hash,$at,$note,$workflow,$inputs,$outputs,$parameters);
""";
        command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$version",version); command.Parameters.AddWithValue("$hash",hash); command.Parameters.AddWithValue("$at",Iso(at)); command.Parameters.AddWithValue("$note",note?.Trim()??string.Empty);
        command.Parameters.AddWithValue("$workflow",JsonSerializer.Serialize(workflow,Json)); command.Parameters.AddWithValue("$inputs",JsonSerializer.Serialize(inputs,Json)); command.Parameters.AddWithValue("$outputs",JsonSerializer.Serialize(outputs,Json)); command.Parameters.AddWithValue("$parameters",JsonSerializer.Serialize(parameters,Json));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<WorkflowModuleVersion>> ListVersionsAsync(SqliteConnection connection,string id,CancellationToken ct)
    {
        var result=new List<WorkflowModuleVersion>();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT version,module_hash,created_at,note,workflow_json,inputs_json,outputs_json,parameters_json FROM workflow_module_versions WHERE module_id=$id ORDER BY version DESC;";
        command.Parameters.AddWithValue("$id",id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) result.Add(ReadVersion(id,reader.GetInt32(0),reader,offset:1));
        return result;
    }

    private static WorkflowModuleVersion ReadVersion(string id,int version,SqliteDataReader reader,int offset=0)
        => new(id,version,reader.GetString(offset),DateTimeOffset.Parse(reader.GetString(offset+1)),reader.GetString(offset+2),
            JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(offset+3),Json)!,
            JsonSerializer.Deserialize<WorkflowModulePort[]>(reader.GetString(offset+4),Json)??[],
            JsonSerializer.Deserialize<WorkflowModulePort[]>(reader.GetString(offset+5),Json)??[],
            JsonSerializer.Deserialize<WorkflowModuleParameter[]>(reader.GetString(offset+6),Json)??[]);

    public async Task DeleteAuthoringRollbackAsync(string id,CancellationToken ct)
    {
        await using var connection=await db.OpenConnectionAsync(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="DELETE FROM workflow_modules WHERE id=$id;";
        command.Parameters.AddWithValue("$id",id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static string ComputeHash(WorkflowDefinition workflow,IReadOnlyList<WorkflowModulePort> inputs,IReadOnlyList<WorkflowModulePort> outputs,IReadOnlyList<WorkflowModuleParameter> parameters)
    {
        var canonical=new
        {
            workflowHash=WorkflowFingerprint.Compute(workflow),
            inputs=inputs.OrderBy(x=>x.Name,StringComparer.Ordinal).Select(x=>new{x.Name,dataType=x.DataType.ToString(),x.Required,x.InternalNodeId,x.InternalPort}),
            outputs=outputs.OrderBy(x=>x.Name,StringComparer.Ordinal).Select(x=>new{x.Name,dataType=x.DataType.ToString(),x.Required,x.InternalNodeId,x.InternalPort}),
            parameters=parameters.OrderBy(x=>x.Name,StringComparer.Ordinal).Select(x=>new{x.Name,x.Type,defaultValue=x.DefaultValue.GetRawText(),x.InternalNodeId,x.InternalParameter})
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical,Json)))).ToLowerInvariant();
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length>96 || id.Any(c=>!(char.IsLetterOrDigit(c)||c is '-' or '_' or '.')))
            throw new ApiValidationException("Module ID must use letters, digits, '-', '_' or '.' and be at most 96 characters.");
    }
    private static string Iso(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
}

public sealed class WorkflowModuleExpander(WorkflowModuleStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxExpansionDepth=12;

    public async Task<ExpandedWorkflowModules> ExpandAsync(WorkflowDefinition workflow,CancellationToken ct=default)
    {
        var current=workflow;
        var dependencies=new List<WorkflowModuleDependency>();
        for(var depth=0;depth<MaxExpansionDepth;depth++)
        {
            var calls=current.Nodes.Where(IsModuleCall).ToArray();
            if(calls.Length==0) return new ExpandedWorkflowModules(current,dependencies);
            current=await ExpandOneLevelAsync(current,calls,dependencies,ct);
        }
        throw new ApiConflictException($"Workflow module expansion exceeded {MaxExpansionDepth} levels. Check for a recursive module dependency.");
    }

    private async Task<WorkflowDefinition> ExpandOneLevelAsync(WorkflowDefinition workflow,IReadOnlyList<NodeDefinition> calls,List<WorkflowModuleDependency> dependencies,CancellationToken ct)
    {
        var resolved=new Dictionary<string,WorkflowModuleVersion>(StringComparer.OrdinalIgnoreCase);
        foreach(var call in calls)
        {
            var moduleId=RequiredString(call,"moduleId");
            var version=RequiredInt(call,"moduleVersion");
            var snapshot=await store.GetVersionAsync(moduleId,version,ct) ?? throw new ApiConflictException($"Module call '{call.Id}' references missing module '{moduleId}' V{version}.");
            var expectedHash=OptionalString(call,"moduleHash");
            if(!string.IsNullOrWhiteSpace(expectedHash)&&!string.Equals(expectedHash,snapshot.ModuleHash,StringComparison.OrdinalIgnoreCase))
                throw new ApiConflictException($"Module call '{call.Id}' hash mismatch for '{moduleId}' V{version}. Expected {expectedHash}, stored {snapshot.ModuleHash}.");
            resolved[call.Id]=snapshot;
            dependencies.Add(new WorkflowModuleDependency(call.Id,moduleId,version,snapshot.ModuleHash));
        }

        var callIds=resolved.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes=new List<NodeDefinition>(workflow.Nodes.Count+resolved.Values.Sum(x=>x.Workflow.Nodes.Count));
        nodes.AddRange(workflow.Nodes.Where(x=>!callIds.Contains(x.Id)));

        foreach(var call in calls)
        {
            var snapshot=resolved[call.Id];
            var parameterOverrides=ResolveParameterOverrides(call,snapshot);
            foreach(var inner in snapshot.Workflow.Nodes)
            {
                var parameters=inner.Parameters is null?new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase):inner.Parameters.ToDictionary(x=>x.Key,x=>x.Value.Clone(),StringComparer.OrdinalIgnoreCase);
                foreach(var mapping in snapshot.Parameters.Where(x=>x.InternalNodeId.Equals(inner.Id,StringComparison.OrdinalIgnoreCase)))
                    if(parameterOverrides.TryGetValue(mapping.Name,out var value)) parameters[mapping.InternalParameter]=value.Clone();
                var position=inner.Position is null?call.Position:new NodePosition((call.Position?.X??0)+inner.Position.X*0.62,(call.Position?.Y??0)+inner.Position.Y*0.62);
                nodes.Add(inner with { Id=Prefix(call.Id,inner.Id), Name=$"{call.Name??snapshot.ModuleId} / {inner.Name??inner.Id}", Position=position, Parameters=parameters });
            }
        }

        var edges=new List<EdgeDefinition>();
        foreach(var edge in workflow.Edges)
        {
            var sourceId=edge.SourceNodeId; var sourcePort=edge.SourcePort; var targetId=edge.TargetNodeId; var targetPort=edge.TargetPort;
            if(resolved.TryGetValue(edge.SourceNodeId,out var sourceModule))
            {
                var mapped=sourceModule.Outputs.FirstOrDefault(x=>x.Name.Equals(edge.SourcePort,StringComparison.OrdinalIgnoreCase)) ?? throw new ApiValidationException($"Module '{sourceModule.ModuleId}' V{sourceModule.Version} has no output '{edge.SourcePort}'.");
                sourceId=Prefix(edge.SourceNodeId,mapped.InternalNodeId); sourcePort=mapped.InternalPort;
            }
            if(resolved.TryGetValue(edge.TargetNodeId,out var targetModule))
            {
                var mapped=targetModule.Inputs.FirstOrDefault(x=>x.Name.Equals(edge.TargetPort,StringComparison.OrdinalIgnoreCase)) ?? throw new ApiValidationException($"Module '{targetModule.ModuleId}' V{targetModule.Version} has no input '{edge.TargetPort}'.");
                targetId=Prefix(edge.TargetNodeId,mapped.InternalNodeId); targetPort=mapped.InternalPort;
            }
            edges.Add(edge with { SourceNodeId=sourceId,SourcePort=sourcePort,TargetNodeId=targetId,TargetPort=targetPort });
        }
        foreach(var call in calls)
        {
            var snapshot=resolved[call.Id];
            edges.AddRange(snapshot.Workflow.Edges.Select(edge=>edge with
            {
                Id=$"{call.Id}::edge::{edge.Id}",
                SourceNodeId=Prefix(call.Id,edge.SourceNodeId),
                TargetNodeId=Prefix(call.Id,edge.TargetNodeId)
            }));
        }
        return workflow with { Nodes=nodes,Edges=edges };
    }

    private static Dictionary<string,JsonElement> ResolveParameterOverrides(NodeDefinition call,WorkflowModuleVersion snapshot)
    {
        var result=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach(var definition in snapshot.Parameters)
        {
            var value=call.Parameters is not null&&call.Parameters.TryGetValue(definition.Name,out var overrideValue)?overrideValue:definition.DefaultValue;
            if(!Compatible(definition.DefaultValue.ValueKind,value.ValueKind)) throw new ApiValidationException($"Module call '{call.Id}' parameter '{definition.Name}' expects {definition.DefaultValue.ValueKind}, got {value.ValueKind}.");
            result[definition.Name]=value.Clone();
        }
        return result;
    }

    public static bool IsModuleCall(NodeDefinition node)=>node.Type.Equals("module.call",StringComparison.OrdinalIgnoreCase);
    private static string Prefix(string instanceId,string innerId)=>$"{instanceId}::{innerId}";
    private static string RequiredString(NodeDefinition node,string key)=>OptionalString(node,key)??throw new ApiValidationException($"Module call '{node.Id}' requires '{key}'.");
    private static string? OptionalString(NodeDefinition node,string key)=>node.Parameters is not null&&node.Parameters.TryGetValue(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static int RequiredInt(NodeDefinition node,string key)
    {
        if(node.Parameters is not null&&node.Parameters.TryGetValue(key,out var value)&&value.TryGetInt32(out var number)&&number>0) return number;
        throw new ApiValidationException($"Module call '{node.Id}' requires positive integer '{key}'.");
    }
    private static bool Compatible(JsonValueKind expected,JsonValueKind actual)=>expected==actual;
}

public sealed class WorkflowModuleAuthoringService(WorkflowModuleStore store,VisionNodeRegistry registry,WorkflowModuleExpander expander,VisionStudio.Engine.Runtime.VisionWorkflowCompiler compiler)
{
    public async Task<ExtractWorkflowModuleResult> ExtractAsync(ExtractWorkflowModuleRequest request,CancellationToken ct)
    {
        if(request.SelectedNodeIds.Count==0) throw new ApiValidationException("Select at least one node to extract a module.");
        var selected=request.SelectedNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedNodes=request.Workflow.Nodes.Where(x=>selected.Contains(x.Id)).ToArray();
        if(selectedNodes.Length!=selected.Count) throw new ApiValidationException("Module selection contains unknown node IDs.");
        if(selectedNodes.Any(WorkflowModuleExpander.IsModuleCall))
        {
            // Nested modules are supported; pinning their immutable version keeps the new module reproducible.
        }

        var internalEdges=request.Workflow.Edges.Where(x=>selected.Contains(x.SourceNodeId)&&selected.Contains(x.TargetNodeId)).ToArray();
        var incoming=request.Workflow.Edges.Where(x=>!selected.Contains(x.SourceNodeId)&&selected.Contains(x.TargetNodeId)).ToArray();
        var outgoing=request.Workflow.Edges.Where(x=>selected.Contains(x.SourceNodeId)&&!selected.Contains(x.TargetNodeId)).ToArray();
        var inputs=BuildInputs(selectedNodes,incoming);
        var outputs=BuildOutputs(selectedNodes,outgoing);
        var parameters=BuildParameters(selectedNodes);
        var normalizedSelectedNodes=NormalizeParameterDefaults(selectedNodes,parameters);
        var moduleWorkflow=new WorkflowDefinition($"module-{request.Id}",request.Name,normalizedSelectedNodes,internalEdges);
        WorkflowModuleStore.ValidateInterface(moduleWorkflow,inputs,outputs,parameters);

        // Validate the source host before mutating persistent module storage.
        var expandedSource=await expander.ExpandAsync(request.Workflow,ct);
        compiler.Compile(expandedSource.Workflow);
        // Resolve nested module references now. Required boundary inputs are intentionally not compiled standalone.
        await expander.ExpandAsync(moduleWorkflow,ct);
        var created=await store.CreateAsync(request.Id,request.Name,request.Description??string.Empty,moduleWorkflow,inputs,outputs,parameters,request.Note??"Initial module version",ct);
        var version=created.Versions.Single(x=>x.Version==1);
        var replacement=BuildReplacementWorkflow(request.Workflow,selected,selectedNodes,incoming,outgoing,version);
        try
        {
            var expanded=await expander.ExpandAsync(replacement,ct);
            compiler.Compile(expanded.Workflow);
            return new ExtractWorkflowModuleResult(created,version,replacement);
        }
        catch
        {
            await store.DeleteAuthoringRollbackAsync(request.Id,CancellationToken.None);
            throw;
        }
    }

    public async Task<WorkflowModuleVersion> AddVersionAsync(string id,SaveWorkflowModuleVersionRequest request,CancellationToken ct)
    {
        var descriptor = await store.GetAsync(id, ct) ?? throw new ApiNotFoundException($"Workflow module '{id}' does not exist.");
        var previous = descriptor.Versions.OrderByDescending(x => x.Version).First();
        ValidateInterfaceAgainstCatalog(request.Workflow, previous.Inputs, previous.Outputs, previous.Parameters);
        // Resolve nested pinned module calls before committing so broken dependencies fail authoring, not Production.
        await expander.ExpandAsync(request.Workflow,ct);
        return await store.AddVersionAsync(id,request.Workflow,request.Note??"Module update",ct);
    }

    private void ValidateInterfaceAgainstCatalog(WorkflowDefinition workflow,IReadOnlyList<WorkflowModulePort> inputs,IReadOnlyList<WorkflowModulePort> outputs,IReadOnlyList<WorkflowModuleParameter> parameters)
    {
        var nodes=workflow.Nodes.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);
        foreach(var port in inputs)
        {
            if(!nodes.TryGetValue(port.InternalNodeId,out var node)) throw new ApiValidationException($"Module input '{port.Name}' references missing node '{port.InternalNodeId}'.");
            var actual=GetPorts(node,true).FirstOrDefault(x=>x.Name.Equals(port.InternalPort,StringComparison.OrdinalIgnoreCase)) ?? throw new ApiValidationException($"Module input '{port.Name}' references missing port '{port.InternalNodeId}.{port.InternalPort}'.");
            if(actual.DataType!=port.DataType) throw new ApiValidationException($"Module input '{port.Name}' changed type from {port.DataType} to {actual.DataType}. V0.52 keeps interface types stable across versions; create a new Module ID for a breaking interface change.");
        }
        foreach(var port in outputs)
        {
            if(!nodes.TryGetValue(port.InternalNodeId,out var node)) throw new ApiValidationException($"Module output '{port.Name}' references missing node '{port.InternalNodeId}'.");
            var actual=GetPorts(node,false).FirstOrDefault(x=>x.Name.Equals(port.InternalPort,StringComparison.OrdinalIgnoreCase)) ?? throw new ApiValidationException($"Module output '{port.Name}' references missing port '{port.InternalNodeId}.{port.InternalPort}'.");
            if(actual.DataType!=port.DataType) throw new ApiValidationException($"Module output '{port.Name}' changed type from {port.DataType} to {actual.DataType}. V0.52 keeps interface types stable across versions; create a new Module ID for a breaking interface change.");
        }
        foreach(var parameter in parameters)
            if(!nodes.TryGetValue(parameter.InternalNodeId,out var node)||node.Parameters is null||!node.Parameters.ContainsKey(parameter.InternalParameter)) throw new ApiValidationException($"Module parameter '{parameter.Name}' target no longer exists.");
    }

    private IReadOnlyList<WorkflowModulePort> BuildInputs(IReadOnlyList<NodeDefinition> selectedNodes,IReadOnlyList<EdgeDefinition> incoming)
    {
        var byId=selectedNodes.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return incoming.Select(edge=>
        {
            var node=byId[edge.TargetNodeId];
            var port=GetPorts(node,true).First(x=>x.Name.Equals(edge.TargetPort,StringComparison.OrdinalIgnoreCase));
            return new WorkflowModulePort(UniquePortName($"{node.Id}.{port.Name}",used),port.DataType,port.Required,node.Id,port.Name);
        }).ToArray();
    }

    private IReadOnlyList<WorkflowModulePort> BuildOutputs(IReadOnlyList<NodeDefinition> selectedNodes,IReadOnlyList<EdgeDefinition> outgoing)
    {
        var byId=selectedNodes.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return outgoing.GroupBy(x=>new{x.SourceNodeId,x.SourcePort}).Select(group=>
        {
            var edge=group.First(); var node=byId[edge.SourceNodeId];
            var port=GetPorts(node,false).First(x=>x.Name.Equals(edge.SourcePort,StringComparison.OrdinalIgnoreCase));
            return new WorkflowModulePort(UniquePortName($"{node.Id}.{port.Name}",used),port.DataType,true,node.Id,port.Name);
        }).ToArray();
    }

    private IReadOnlyList<PortDescriptor> GetPorts(NodeDefinition node,bool inputs)
    {
        if(!WorkflowModuleExpander.IsModuleCall(node))
        {
            var catalog=registry.Require(node.Type).Catalog;
            return inputs?catalog.Inputs:catalog.Outputs;
        }
        var key=inputs?"__moduleInputs":"__moduleOutputs";
        if(node.Parameters is null||!node.Parameters.TryGetValue(key,out var value))
            throw new ApiValidationException($"Nested module call '{node.Id}' is missing embedded interface metadata.");
        return JsonSerializer.Deserialize<PortDescriptor[]>(value.GetRawText(),Json)??[];
    }

    private static IReadOnlyList<NodeDefinition> NormalizeParameterDefaults(IReadOnlyList<NodeDefinition> nodes,IReadOnlyList<WorkflowModuleParameter> parameters)
    {
        var byNode=parameters.GroupBy(x=>x.InternalNodeId,StringComparer.OrdinalIgnoreCase).ToDictionary(x=>x.Key,x=>x.ToArray(),StringComparer.OrdinalIgnoreCase);
        return nodes.Select(node=>
        {
            if(!byNode.TryGetValue(node.Id,out var mapped)) return node;
            var values=node.Parameters is null?new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase):node.Parameters.ToDictionary(x=>x.Key,x=>x.Value.Clone(),StringComparer.OrdinalIgnoreCase);
            foreach(var parameter in mapped) if(!values.ContainsKey(parameter.InternalParameter)) values[parameter.InternalParameter]=parameter.DefaultValue.Clone();
            return node with{Parameters=values};
        }).ToArray();
    }

    private IReadOnlyList<WorkflowModuleParameter> BuildParameters(IReadOnlyList<NodeDefinition> selectedNodes)
    {
        var result=new List<WorkflowModuleParameter>();
        foreach(var node in selectedNodes)
        {
            if(WorkflowModuleExpander.IsModuleCall(node)) continue;
            var catalog=registry.Require(node.Type).Catalog;
            foreach(var descriptor in catalog.Parameters)
            {
                JsonElement value;
                if(node.Parameters is not null&&node.Parameters.TryGetValue(descriptor.Name,out var explicitValue)) value=explicitValue.Clone();
                else value=JsonSerializer.SerializeToElement(descriptor.DefaultValue,Json);
                result.Add(new WorkflowModuleParameter($"{node.Id}.{descriptor.Name}",$"{node.Name??catalog.DisplayName} / {descriptor.Label}",descriptor.Type,value,node.Id,descriptor.Name,descriptor.Min,descriptor.Max,descriptor.Step,descriptor.Options,descriptor.Unit,descriptor.Group,descriptor.Description));
            }
        }
        return result;
    }

    private static string SafeInstanceToken(string value)
        => new(value.Select(ch=>char.IsLetterOrDigit(ch)||ch is '_' or '-'?ch:'_').ToArray());

    private static WorkflowDefinition BuildReplacementWorkflow(WorkflowDefinition source,HashSet<string> selected,IReadOnlyList<NodeDefinition> selectedNodes,IReadOnlyList<EdgeDefinition> incoming,IReadOnlyList<EdgeDefinition> outgoing,WorkflowModuleVersion module)
    {
        var centroid=new NodePosition(selectedNodes.Average(x=>x.Position?.X??0),selectedNodes.Average(x=>x.Position?.Y??0));
        var instanceId=$"module-{SafeInstanceToken(module.ModuleId)}-{Guid.NewGuid().ToString("N")[..8]}";
        var parameters=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["moduleId"]=JsonSerializer.SerializeToElement(module.ModuleId,Json),
            ["moduleVersion"]=JsonSerializer.SerializeToElement(module.Version,Json),
            ["moduleHash"]=JsonSerializer.SerializeToElement(module.ModuleHash,Json),
            ["__moduleInputs"]=JsonSerializer.SerializeToElement(module.Inputs.Select(ToPortDescriptor).ToArray(),Json),
            ["__moduleOutputs"]=JsonSerializer.SerializeToElement(module.Outputs.Select(ToPortDescriptor).ToArray(),Json),
            ["__moduleParameters"]=JsonSerializer.SerializeToElement(module.Parameters,Json)
        };
        foreach(var parameter in module.Parameters) parameters[parameter.Name]=parameter.DefaultValue.Clone();
        var call=new NodeDefinition(instanceId,"module.call",module.ModuleId,centroid,parameters);
        var nodes=source.Nodes.Where(x=>!selected.Contains(x.Id)).Append(call).ToArray();
        var edges=source.Edges.Where(x=>!selected.Contains(x.SourceNodeId)&&!selected.Contains(x.TargetNodeId)).ToList();
        foreach(var edge in incoming)
        {
            var mapped=module.Inputs.First(x=>x.InternalNodeId.Equals(edge.TargetNodeId,StringComparison.OrdinalIgnoreCase)&&x.InternalPort.Equals(edge.TargetPort,StringComparison.OrdinalIgnoreCase));
            edges.Add(edge with{TargetNodeId=instanceId,TargetPort=mapped.Name});
        }
        foreach(var edge in outgoing)
        {
            var mapped=module.Outputs.First(x=>x.InternalNodeId.Equals(edge.SourceNodeId,StringComparison.OrdinalIgnoreCase)&&x.InternalPort.Equals(edge.SourcePort,StringComparison.OrdinalIgnoreCase));
            edges.Add(edge with{SourceNodeId=instanceId,SourcePort=mapped.Name});
        }
        return source with{Nodes=nodes,Edges=edges};
    }

    public static NodeDefinition CreateCallNode(WorkflowModuleVersion module,string id,NodePosition position)
    {
        var parameters=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["moduleId"]=JsonSerializer.SerializeToElement(module.ModuleId,Json),["moduleVersion"]=JsonSerializer.SerializeToElement(module.Version,Json),["moduleHash"]=JsonSerializer.SerializeToElement(module.ModuleHash,Json),
            ["__moduleInputs"]=JsonSerializer.SerializeToElement(module.Inputs.Select(ToPortDescriptor).ToArray(),Json),["__moduleOutputs"]=JsonSerializer.SerializeToElement(module.Outputs.Select(ToPortDescriptor).ToArray(),Json),["__moduleParameters"]=JsonSerializer.SerializeToElement(module.Parameters,Json)
        };
        foreach(var parameter in module.Parameters) parameters[parameter.Name]=parameter.DefaultValue.Clone();
        return new NodeDefinition(id,"module.call",module.ModuleId,position,parameters);
    }

    private static object ToPortDescriptor(WorkflowModulePort port)=>new{port.Name,dataType=port.DataType.ToString(),port.Required};
    private static string UniquePortName(string seed,HashSet<string> used)
    {
        var normalized=new string(seed.Select(c=>char.IsLetterOrDigit(c)||c is '_' or '-' or '.'?c:'_').ToArray());
        var candidate=normalized; var suffix=2;
        while(!used.Add(candidate)) candidate=$"{normalized}_{suffix++}";
        return candidate;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

public sealed class ModuleAwareVisionWorkflowRunner(WorkflowModuleExpander expander,VisionStudio.Engine.Runtime.WorkflowCoreVisionRunner inner) : VisionStudio.Engine.Runtime.IVisionWorkflowRunner
{
    public async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow,VisionRunOptions? options=null,string? runId=null,CancellationToken cancellationToken=default)
    {
        options??=new VisionRunOptions();
        if(options.Mode!=DebugRunMode.Full)
        {
            var moduleIds=workflow.Nodes.Where(WorkflowModuleExpander.IsModuleCall).Select(x=>x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if((options.TargetNodeId is not null&&moduleIds.Contains(options.TargetNodeId))||(options.Breakpoints??[]).Any(moduleIds.Contains))
                return new WorkflowRunResult(runId??Guid.NewGuid().ToString("N"),false,0,false,0,0,[],[],null,"Module calls are expanded before execution. Open the module in Reusable Modules to debug its internal nodes; module-level single-step is not supported in V0.52.","Error");
        }
        var expanded=await expander.ExpandAsync(workflow,cancellationToken);
        return await inner.RunAsync(expanded.Workflow,options,runId,cancellationToken);
    }
}
