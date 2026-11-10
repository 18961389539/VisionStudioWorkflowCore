using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// P0 M4：内联模型 → 资产的抽取服务。验证"只改两个参数、字节不变、可逆"，
/// 以及重复抽取/非形状节点必须明确失败。用临时内容根，不写仓库 data 目录。
/// </summary>
public sealed class ShapeModelAssetExtractionTests
{
    private static async Task<(TempWebHostEnvironment Env, ShapeModelAssetStore Store, WorkflowStore Workflows, ShapeModelAssetExtraction Service)>
        CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new ShapeModelAssetStore(new AssetStore(db));
        var workflows = new WorkflowStore(env);
        return (env, store, workflows, new ShapeModelAssetExtraction(store, workflows));
    }

    private static NodeDefinition ShapeNode(string id, byte[] model, string nodeType = "match.shape")
        => new(id, nodeType, null, null, new Dictionary<string, JsonElement>
        {
            ["modelMode"] = JsonSerializer.SerializeToElement("trained"),
            ["trainedModelData"] = JsonSerializer.SerializeToElement(Convert.ToBase64String(model)),
            ["trainedModelConfig"] = JsonSerializer.SerializeToElement("cfg-v1"),
            ["minScore"] = JsonSerializer.SerializeToElement(0.6)
        });

    [Fact]
    public async Task Extract_CreatesAssetAndSwitchesTheNodeToIt()
    {
        var (env, store, workflows, service) = await CreateAsync();
        using var _ = env;
        var model = new byte[128];
        for (var i = 0; i < model.Length; i++) model[i] = (byte)(i + 3);
        await workflows.SaveAsync("wf-1", new WorkflowDefinition("wf-1", "Demo", [ShapeNode("shape-1", model)], []), CancellationToken.None);

        var result = await service.ExtractAsync("wf-1", "shape-1", "Demo shape", null, "tester", CancellationToken.None);

        Assert.Equal(1, result.Version);
        Assert.Equal(model, await store.ReadBytesAsync(result.AssetId, 1, CancellationToken.None));

        var reloaded = await workflows.LoadAsync("wf-1", CancellationToken.None);
        var node = Assert.Single(reloaded!.Nodes);
        Assert.Equal(result.AssetId, node.Parameters!["modelAssetId"].GetString());
        Assert.Equal(1, node.Parameters["modelAssetVersion"].GetInt32());
        Assert.False(node.Parameters.ContainsKey("trainedModelData"));
        // 其余参数原样保留：迁移只动两个键。
        Assert.Equal("cfg-v1", node.Parameters["trainedModelConfig"].GetString());
        Assert.Equal(0.6, node.Parameters["minScore"].GetDouble());
    }

    [Fact]
    public async Task Extract_SecondTimeFailsBecauseTheInlineModelIsGone()
    {
        var (env, _, workflows, service) = await CreateAsync();
        using var _ = env;
        await workflows.SaveAsync("wf-2", new WorkflowDefinition("wf-2", "Demo", [ShapeNode("shape-2", new byte[64])], []), CancellationToken.None);
        await service.ExtractAsync("wf-2", "shape-2", null, null, null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ApiValidationException>(() =>
            service.ExtractAsync("wf-2", "shape-2", null, null, null, CancellationToken.None));
        Assert.Contains("no inline trainedModelData", error.Message);
    }

    [Fact]
    public async Task Extract_RejectsNonShapeNodesAndUnknownTargets()
    {
        var (env, _, workflows, service) = await CreateAsync();
        using var _ = env;
        await workflows.SaveAsync("wf-3", new WorkflowDefinition("wf-3", "Demo",
            [ShapeNode("threshold-1", new byte[32], nodeType: "image.threshold")], []), CancellationToken.None);

        var wrongType = await Assert.ThrowsAsync<ApiValidationException>(() =>
            service.ExtractAsync("wf-3", "threshold-1", null, null, null, CancellationToken.None));
        Assert.Contains("only match.shape", wrongType.Message);

        var missingNode = await Assert.ThrowsAsync<ApiNotFoundException>(() =>
            service.ExtractAsync("wf-3", "nope", null, null, null, CancellationToken.None));
        Assert.Contains("no node", missingNode.Message);

        var missingWorkflow = await Assert.ThrowsAsync<ApiNotFoundException>(() =>
            service.ExtractAsync("wf-missing", "shape-1", null, null, null, CancellationToken.None));
        Assert.Contains("does not exist", missingWorkflow.Message);
    }
}
