using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// Built-in node executors are discovered from the engine assembly instead of being enumerated by hand.
///
/// Before this, one node required three synchronized edits: the catalog declaration, a
/// <c>services.AddSingleton&lt;TNode&gt;()</c> line and a <c>services.GetRequiredService&lt;TNode&gt;()</c>
/// line in the bootstrap list. Forgetting the bootstrap entry produced a host that started normally and
/// only failed when a workflow first executed the node ("type is not registered"), which is the worst
/// possible moment on a production line. Discovery keeps the catalog type declaration as the single
/// source of truth and turns any mismatch into a startup failure that names every offending type.
/// </summary>
public static class BuiltInNodeExecutors
{
    /// <summary>Registers every discoverable built-in executor as a singleton <see cref="IVisionNodeExecutor"/>.</summary>
    public static IServiceCollection AddBuiltInNodeExecutors(this IServiceCollection services)
    {
        foreach (var implementation in Discover())
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IVisionNodeExecutor), implementation));
            // 具体类型也必须可解析：CalibrationWizardService 等消费者按具体节点类构造注入。
            // 工厂从枚举集合取回同一个实例，避免再 new 一份导致“单例”语义分裂。
            services.TryAddSingleton(implementation, provider => provider.GetServices<IVisionNodeExecutor>()
                .First(executor => executor.GetType() == implementation));
        }
        return services;
    }

    /// <summary>Concrete, publicly constructible node executors shipped with the engine assembly.</summary>
    public static IReadOnlyList<Type> Discover() =>
        typeof(VisionNodeRegistry).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(type => typeof(IVisionNodeExecutor).IsAssignableFrom(type))
            .Where(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Returns every inconsistency between the built-in catalog and the resolved executors.
    /// Empty means the catalog can be served and every declared type can actually run.
    /// </summary>
    public static IReadOnlyList<string> FindDrift(
        IEnumerable<IVisionNodeExecutor> executors,
        IEnumerable<NodeCatalogItem> catalog)
    {
        var problems = new List<string>();
        var executorTypes = executors.Select(executor => executor.Type).ToArray();
        var catalogTypes = catalog.Select(item => item.Type).ToArray();

        foreach (var duplicate in executorTypes
                     .GroupBy(type => type, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            problems.Add($"executor type '{duplicate.Key}' is registered {duplicate.Count()} times");

        foreach (var duplicate in catalogTypes
                     .GroupBy(type => type, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            problems.Add($"catalog type '{duplicate.Key}' is declared {duplicate.Count()} times");

        var executorSet = executorTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogSet = catalogTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var missing in catalogSet.Except(executorSet, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
            problems.Add($"catalog type '{missing}' has no executor");

        // 已合并节点的别名执行器不在目录里：它们只服务历史文档，算法转发给规范节点
        // （见 Nodes/MergedNodeAliases.cs 与 NodeTypeMigration）。除此之外的编外执行器仍是错误。
        var aliasTypes = NodeTypeMigration.ReplacementMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unknown in executorSet.Except(catalogSet, StringComparer.OrdinalIgnoreCase)
                     .Where(type => !aliasTypes.Contains(type))
                     .OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
            problems.Add($"executor type '{unknown}' is not declared in the built-in catalog");

        foreach (var staleAlias in aliasTypes.Except(executorSet, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
            problems.Add($"merged-node alias '{staleAlias}' declares no executor");

        return problems;
    }
}
