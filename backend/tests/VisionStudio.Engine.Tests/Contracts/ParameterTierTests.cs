using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 参数首屏分层守卫（2026-11-06）。这组用例锁住三件事：
/// 1. 任何节点的默认可见、未折叠参数都不超过 <see cref="ParameterTierCatalog.EssentialLimit"/> 个；
/// 2. 覆盖表只能引用目录里真实存在的非内部参数，且不能超过首屏预算；
/// 3. 内部参数（ROI / 回放路径等宿主资产）永远不能被标记为高级参数。
/// </summary>
public sealed class ParameterTierTests
{
    private static readonly IReadOnlyList<NodeCatalogItem> Catalog = BuiltInNodeCatalog.Items;

    [Fact]
    public void DefaultVisibleParameters_NeverExceedTheEssentialLimit()
    {
        var offenders = Catalog
            .Select(item => new
            {
                item.Type,
                Visible = item.Parameters.Count(parameter =>
                    !parameter.Advanced && ParameterTierCatalog.IsVisibleByDefault(parameter, item.Parameters))
            })
            .Where(entry => entry.Visible > ParameterTierCatalog.EssentialLimit)
            .Select(entry => $"{entry.Type}={entry.Visible}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void EssentialOverrides_OnlyReferenceExistingNonInternalParameters()
    {
        foreach (var (type, names) in ParameterTierCatalog.EssentialOverrides)
        {
            var item = Catalog.SingleOrDefault(candidate =>
                candidate.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            Assert.True(item is not null, $"{type} 不在目录中，覆盖表会静默失效。");
            Assert.True(names.Count <= ParameterTierCatalog.EssentialLimit,
                $"{type} 的核心参数超过 {ParameterTierCatalog.EssentialLimit} 个。");

            var known = item!.Parameters
                .Where(parameter => !parameter.Internal)
                .Select(parameter => parameter.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = names.Where(name => !known.Contains(name)).ToArray();
            Assert.True(unknown.Length == 0, $"{type} 的覆盖表引用了不存在的参数：{string.Join(", ", unknown)}");

            // 覆盖表只该用在真正超出首屏预算的节点；否则说明参数已合并/删除，表该清理了。
            var defaultVisible = item.Parameters.Count(parameter =>
                ParameterTierCatalog.IsVisibleByDefault(parameter, item.Parameters));
            Assert.True(defaultVisible > ParameterTierCatalog.EssentialLimit,
                $"{type} 默认只可见 {defaultVisible} 个参数，不需要覆盖表。");
        }
    }

    [Fact]
    public void NodesBeyondTheLimit_MarkAtLeastOneParameterAdvanced()
    {
        foreach (var item in Catalog)
        {
            var defaultVisible = item.Parameters.Count(parameter =>
                ParameterTierCatalog.IsVisibleByDefault(parameter, item.Parameters));
            if (defaultVisible <= ParameterTierCatalog.EssentialLimit) continue;

            Assert.True(item.Parameters.Any(parameter => parameter.Advanced),
                $"{item.Type} 有 {defaultVisible} 个默认可见参数却没有折叠任何参数。");
        }
    }

    [Fact]
    public void AdvancedParameters_AreNeverInternal()
    {
        Assert.All(Catalog.SelectMany(item => item.Parameters),
            parameter => Assert.False(parameter.Internal && parameter.Advanced));
    }
}
