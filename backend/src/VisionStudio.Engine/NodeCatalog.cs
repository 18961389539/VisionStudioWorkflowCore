namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static readonly PortDescriptor ExecIn = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    private static readonly IReadOnlyList<NodeCatalogItem> RawItems =
    [
        .. AcquisitionItems,
        .. ImageProcessingItems,
        .. CoordinateItems,
        .. DeviceItems,
        .. ValueFlowItems,
    ];

    // Compute-only built-ins explicitly declare absence of device actions, including for Try validation.
    // File-backed template matching stays outside automatic retry because its file can change between attempts.
    /// <summary>
    /// 未经 <see cref="OperatorParameterForms"/> / 本地 ROI 参数声明后处理的目录原文。
    /// 仅供契约测试使用：发布目录必须逐端口等于这份原文（后处理不得改写输出端口）。
    /// </summary>
    internal static IReadOnlyList<NodeCatalogItem> RawCatalog => RawItems;

    public static readonly IReadOnlyList<NodeCatalogItem> Items = RawItems
        .Select(NodeExecutionModeCatalog.Apply)
        .Select(item => item.Type == "match.template"
            ? item with { Capabilities = (item.Capabilities ?? new VisionToolCapabilities()) with { Deterministic = false } }
            : item.Actions is null && Runtime.VisionExecutionPolicy.IsPipelineEligible(item)
                ? item with { Actions = [] }
                : item)
        .Select(OperatorParameterForms.Apply)
        .Select(DeclareLocalRoiParameters)
        .Select(ParameterTierCatalog.Apply)
        .Select(NodeMaturityCatalog.Apply)
        .ToArray();

    /// <summary>
    /// 执行器通过 <c>NodeDefinition.GetRoi(parameterName)</c> 读取的本地 ROI 参数名。
    /// 目录声明与编译期校验共用这一处，避免"目录里没有、运行时却在读"的漂移。
    /// </summary>
    public static IReadOnlyList<string> RoiParameterNames(string type, VisionToolCapabilities? capabilities)
    {
        var names = new List<string>(3);
        if (capabilities?.SupportsRoi == true) names.Add("roi");
        if (type.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
            names.AddRange(["searchRoi", "templateRoi", "clutterRoi"]);
        if (type.Equals("match.template", StringComparison.OrdinalIgnoreCase))
            names.AddRange(["searchRoi", "templateRoi", "ignoreRoi"]);
        if (type.Equals("match.feature", StringComparison.OrdinalIgnoreCase))
            names.Add("templateRoi");
        return names;
    }

    private static NodeCatalogItem DeclareLocalRoiParameters(NodeCatalogItem item)
    {
        var declared = item.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RoiParameterNames(item.Type, item.Capabilities)
            .Where(name => !declared.Contains(name))
            .Select(name => new ParameterDescriptor(name, RoiLabel(name), "roi", Group: "ROI", Internal: true,
                Description: RoiDescription(item.Type, name)))
            .ToArray();
        return missing.Length == 0 ? item : item with { Parameters = [.. item.Parameters, .. missing] };
    }

    private static string RoiDescription(string nodeType, string name) => (nodeType, name) switch
    {
        ("match.template", "templateRoi") =>
            "Canvas-authored template mask. Applied with ReduceDomain before JLVision CreateNccModel so FindNccModel uses only masked template pixels (HALCON-style; no separate Find mask arg). Distinct from scene ignoreRoi. Edited through the ROI overlay.",
        ("match.template", "ignoreRoi") =>
            "Canvas-authored scene ignore region. Hard mode ReduceDomains the search image; soft mode mean-lerps (approximate — not soft scene-weighted NCC). Distinct from templateRoi.",
        ("match.template", "searchRoi") =>
            "Canvas-authored search window in full-image coordinates. Edited through the ROI overlay, not as a form field.",
        ("match.feature", "templateRoi") =>
            "Canvas-authored template mask for feature extraction. Keypoints/descriptors are computed only inside it, so background texture next to the part cannot pull the estimated transform. Distinct from the scene roi port.",
        _ => "Canvas-authored ROI object stored on the node. Edited through the ROI overlay, not as a form field."
    };

    private static string RoiLabel(string name) => name switch
    {
        "searchRoi" => "Search ROI",
        "templateRoi" => "Template ROI",
        "ignoreRoi" => "Ignore ROI",
        "clutterRoi" => "Clutter ROI",
        _ => "ROI"
    };

    public static NodeCatalogItem Require(string type) =>
        Items.FirstOrDefault(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Built-in catalog item '{type}' was not found.");
}
