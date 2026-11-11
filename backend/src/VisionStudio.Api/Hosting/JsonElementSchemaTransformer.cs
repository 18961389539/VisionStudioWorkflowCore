using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// JsonElement / object 载荷（节点参数、摘要、任意 JSON）默认生成 "object 无属性" 的 schema，
/// openapi-typescript 会把它变成 <c>Record&lt;string, never&gt;</c>，前端无法用生成类型表示工作流参数与节点摘要。
/// 处理规则（2026-11-10）：
/// <list type="bullet">
/// <item>直接出现的 JsonElement / object → 空 schema（语义为任意 JSON，生成 TS 的 unknown）；</item>
/// <item>Dictionary / IReadOnlyDictionary&lt;string, JsonElement|object&gt; → object + additionalProperties 空 schema，
/// 生成 Record&lt;string, unknown&gt;。</item>
/// </list>
/// </summary>
internal sealed class JsonElementSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(JsonElement) || type == typeof(JsonElement?) || type == typeof(object))
        {
            ResetToAny(schema);
            return Task.CompletedTask;
        }

        if (IsFreeFormDictionary(type))
        {
            // 保留原有类型位（Dictionary 可能声明为 nullable），只补 object 与自由值。
            schema.Type = schema.Type is { } current ? current | JsonSchemaType.Object : JsonSchemaType.Object;
            schema.Properties?.Clear();
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = new OpenApiSchema();
        }
        return Task.CompletedTask;
    }

    private static bool IsFreeFormDictionary(Type type)
    {
        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        if (arguments.Length != 2) return false;
        if (arguments[1] != typeof(JsonElement) && arguments[1] != typeof(object)) return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Dictionary<,>)
            || definition == typeof(IDictionary<,>)
            || definition == typeof(IReadOnlyDictionary<,>)
            || typeof(IDictionary<,>).MakeGenericType(arguments).IsAssignableFrom(type)
            || typeof(IReadOnlyDictionary<,>).MakeGenericType(arguments).IsAssignableFrom(type);
    }

    private static void ResetToAny(OpenApiSchema schema)
    {
        schema.Type = null;
        schema.Format = null;
        schema.Properties?.Clear();
        schema.Required?.Clear();
        schema.Enum?.Clear();
        schema.AdditionalProperties = null;
        schema.AdditionalPropertiesAllowed = true;
    }
}
