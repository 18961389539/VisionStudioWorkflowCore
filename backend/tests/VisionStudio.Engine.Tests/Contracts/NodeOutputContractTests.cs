namespace VisionStudio.Engine.Tests;

/// <summary>
/// 发布目录必须逐端口等于源码目录。目录后处理里不再允许任何"擦端口 / 改必需位"的规则。
///
/// 加这条守卫的原因：这里曾经有三条规则在发布时改写契约——<c>Unimplemented</c> 名单
/// （coordinate.distance 的 frame/unit/coincident、geometry.angle 的 degrees/radians/quadrant、
/// flow.parallel 的 branchCount/joined 等）、<c>OverlayProducers</c> 白名单（118 个节点声明了从不产出的
/// overlay 端口，靠它丢弃）、以及按节点类型放宽 Required（image.colorConvert 的 channel2/3）。
/// 结果是源码说一套端口、/api/catalog 说另一套，名单还会过期（flow.loopEnd 的端口早被删掉，名单仍在擦）。
/// 现在凡是被额外剥离或改写的端口都会在这里失败，强制"要么实现它，要么从目录里删掉"。
/// </summary>
public sealed class NodeOutputContractTests
{
    [Fact]
    public void PublishedOutputsAreExactlyTheDeclaredOutputs()
    {
        var raw = BuiltInNodeCatalog.RawCatalog;
        var published = BuiltInNodeCatalog.Items.ToDictionary(item => item.Type, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(raw.Count, published.Count);

        var violations = new List<string>();
        foreach (var source in raw)
        {
            var target = published[source.Type];
            if (source.Outputs.Count != target.Outputs.Count)
            {
                violations.Add($"{source.Type} 声明 {source.Outputs.Count} 个输出端口，发布 {target.Outputs.Count} 个。");
                continue;
            }

            for (var index = 0; index < source.Outputs.Count; index++)
            {
                var declared = source.Outputs[index];
                var actual = target.Outputs[index];
                if (!declared.Name.Equals(actual.Name, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{source.Type} 第 {index + 1} 个输出端口：声明 '{declared.Name}'，发布 '{actual.Name}'。");
                else if (declared.Required != actual.Required)
                    violations.Add($"{source.Type}.{declared.Name} 的 Required 被后处理从 {declared.Required} 改成了 {actual.Required}；" +
                                   "条件端口应直接在目录里声明 Required: false / RequiredWhen。");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
