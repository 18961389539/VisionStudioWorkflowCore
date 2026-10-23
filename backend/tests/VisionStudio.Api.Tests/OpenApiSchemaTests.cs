using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Engine;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 契约相关守卫：这些 DTO 通过 <c>Produces&lt;T&gt;()</c> 进入 OpenAPI 文档（P1-4 契约单一真相源）。
/// System.Text.Json 的 schema 导出器对某些构造参数默认值会抛异常（已知坑：非 nullable struct 参数
/// 写 <c>= default</c> 时元数据里默认值为 null，导出器用非 nullable 的 T 去 unbox）。
/// 一旦某个 DTO 无法生成 schema，<c>/openapi/v1.json</c> 会整体 400，前端类型生成与契约测试全断。
/// 因此这里逐个类型直接导出 schema——失败时能立刻指出是哪个类型，而不是只看到整份文档 500。
/// </summary>
public class OpenApiSchemaTests
{
    public static IEnumerable<object[]> ExposedDtos() =>
    [
        [typeof(DeviceDescriptor)],
        [typeof(NodeCatalogItem)],
        [typeof(RobotDescriptor)],
        [typeof(JobDescriptor)],
        [typeof(AlarmRecord)],
        [typeof(RunTraceRecord)],
        [typeof(TracePage)],
        [typeof(WorkflowRunResult)]
    ];

    [Theory]
    [MemberData(nameof(ExposedDtos))]
    public void Dto_CanProduceJsonSchema(Type type)
    {
        // JsonSchemaExporter 要求 options 显式带 TypeInfoResolver（否则 ValidateOptions 直接抛）。
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(options, type);
        Assert.NotNull(node);
    }
}
