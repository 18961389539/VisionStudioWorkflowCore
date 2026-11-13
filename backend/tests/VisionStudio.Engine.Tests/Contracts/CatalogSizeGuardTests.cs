namespace VisionStudio.Engine.Tests;

/// <summary>
/// 目录规模的度量下限守卫（2026-10-29）。
///
/// 为什么是"下限"而不是"精确值"：新增节点是常态，把测试改一次就能过，守卫价值不大；
/// 真正的风险是合并/重构/误删文件把节点悄悄吃掉——目录从 242 掉到 240，没有任何测试会红，
/// 但工具箱少两个入口，现场只会反馈"功能不见了"，且已保存的流程会加载失败。
///
/// 所以：允许增加，禁止减少。减少节点必须是一次显式的、连下面常量一起改的提交，评审看得见。
///
/// 与 <c>BuiltInNodeRegistrationTests.HostExecutors_MatchTheBuiltInCatalog</c> 的关系：
/// 那个测试保证"目录条目 ↔ 执行器"双向精确匹配；本测试独立守住目录本身的规模，
/// 即使某次重构把目录和执行器一起删掉（双向匹配仍然成立），这里也会红。
/// </summary>
public sealed class CatalogSizeGuardTests
{
    /// <summary>
    /// 2026-10-29 的记录值（242）。这是棘轮：只升不降。
    /// 合并重复节点是有意的目录缩减时，在同一次提交里下调此常量，并在提交说明里写清合并映射。
    /// </summary>
    private const int MinimumNodeCount = 242;

    [Fact]
    public void BuiltInCatalog_DoesNotShrinkBelowTheRecordedFloor()
    {
        var actual = BuiltInNodeCatalog.Items.Count;

        Assert.True(
            actual >= MinimumNodeCount,
            $"Built-in catalog shrank to {actual} nodes, floor is {MinimumNodeCount}. "
            + "Removing a node breaks every saved workflow that references it — either restore the node, "
            + "or lower MinimumNodeCount in the same commit and document the migration in NodeTypeMigration.");
    }

    [Fact]
    public void BuiltInCatalog_DeclaresEveryTypeExactlyOnce()
    {
        // 同一 type 出现两次时，注册表会用后者覆盖前者（或直接抛重复注册），
        // 而前端工具箱会渲染两个一模一样的入口——两种表现都很难从界面上看出根因。
        var duplicates = BuiltInNodeCatalog.Items
            .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void BuiltInCatalog_KeepsEveryNodeDiscoverable()
    {
        // 空描述/空分类的节点会以"未命名"出现在工具箱里，等于从界面上消失。
        // 节点数够、但用户找不到，与删掉它没有区别。
        var unnamed = BuiltInNodeCatalog.Items
            .Where(item => string.IsNullOrWhiteSpace(item.DisplayName) || string.IsNullOrWhiteSpace(item.Category))
            .Select(item => item.Type)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unnamed);
    }
}
