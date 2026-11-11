using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// 把 C# 中**非空**的构造属性补成 OpenAPI required（2026-11-10）。
///
/// .NET OpenAPI 会把"带默认值的构造参数"和"只读计算属性"一律标成 optional，
/// 但序列化时它们总会出现在响应里（如 CameraSettings.triggerMode、RunCounterPersistenceStatus.degraded）。
/// 生成类型因此比实际响应宽松，前端只能保持 required 并把它当作已知 schema 误差。
/// 这里按 CLR 可空性补 required；带条件序列化（如 JsonIgnore WhenWritingDefault）的属性保持不动。
/// </summary>
internal sealed class RequiredPropertiesSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly NullabilityInfoContext Nullability = new();

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var jsonType = context.JsonTypeInfo;
        if (jsonType.Kind != JsonTypeInfoKind.Object || schema.Properties is not { Count: > 0 } properties)
            return Task.CompletedTask;

        schema.Required ??= new HashSet<string>();
        var required = schema.Required;
        // 只看类 DTO：record struct 不会进入这个 transformer（见 StructSchemaRequiredDocumentTransformer）。
        foreach (var property in jsonType.Properties)
        {
            if (!properties.ContainsKey(property.Name)) continue;
            if (property.AttributeProvider is not PropertyInfo clr) continue;
            if (clr.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: not JsonIgnoreCondition.Never }) continue;

            var nonNullable = clr.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(clr.PropertyType) is null
                : Nullability.Create(clr).ReadState == NullabilityState.NotNull;
            if (nonNullable) required.Add(property.Name);
        }

        schema.Required = required;
        return Task.CompletedTask;
    }
}
