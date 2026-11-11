using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// ASP.NET OpenAPI 不会对 readonly record struct 调用 schema transformer（2026-11-10 实测），
/// 这些 schema 的属性因此保持 optional，尽管默认序列化总会输出它们。
/// 文档级补丁：把已知 struct schema 的全部属性标记为 required。
/// 新增暴露的 struct DTO 时，把 schema 名称加进 <see cref="ValueTypeSchemaNames"/>。
/// </summary>
internal sealed class StructSchemaRequiredDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly string[] ValueTypeSchemaNames = ["VisionRobotTarget2D"];

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (document.Components?.Schemas is not { } schemas) return Task.CompletedTask;

        foreach (var name in ValueTypeSchemaNames)
        {
            if (!schemas.TryGetValue(name, out var schema) || schema is not OpenApiSchema concrete
                || concrete.Properties is not { Count: > 0 } properties) continue;
            concrete.Required ??= new HashSet<string>();
            foreach (var property in properties.Keys) concrete.Required.Add(property);
        }
        return Task.CompletedTask;
    }
}
