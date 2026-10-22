using System.Text.Json;
using System.Text.RegularExpressions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Catalog contract tests: unique node types, documented user parameters, no retired controls,
/// and cross-cutting diagnostics/retry options only where the executor implements them.
/// </summary>
public sealed class NodeCatalogContractTests
{
    private static readonly HashSet<string> RetiredParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "strictMode", "bufferPolicy", "strictId", "defaultPolicy",
        "missingAPolicy", "strictMissingB", "directionPolicy", "reportMode",
        "requireFiniteScores", "outOfRangePolicy"
    };

    private static readonly HashSet<string> DiagnosticsLevelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.dynamicThreshold", "image.difference", "measure.regionIntensity", "image.flatFieldCorrect",
        "measure.caliper", "output.saveImage", "output.exportResult", "output.httpRequest",
        "match.template", "match.shape"
    };

    private static readonly HashSet<string> RetryPolicyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "output.saveImage", "output.exportResult", "output.httpRequest"
    };

    private static readonly HashSet<string> OverlayProducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "inference.obb", "inference.segmentation", "inference.instanceSegmentation", "inference.pose",
        "jlvision.contourOp", "jlvision.contourFeature", "jlvision.geometry", "jlvision.homMat",
        "jlvision.matrixOp", "jlvision.measure1d", "jlvision.imageOp", "jlvision.regionFeature",
        "jlvision.regionOp", "jlvision.threshold"
    };

    [Fact]
    public void Catalog_NodeTypesAreUnique()
    {
        var duplicates = BuiltInNodeCatalog.Items.GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.Empty(duplicates);
        Assert.Equal(BuiltInNodeCatalog.Items.Count, BuiltInNodeCatalog.Items.Select(item => item.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Catalog_UserParametersAreDocumented()
    {
        var missing = BuiltInNodeCatalog.Items
            .SelectMany(item => item.Parameters
                .Where(parameter => !parameter.Internal && string.IsNullOrWhiteSpace(parameter.Description))
                .Select(parameter => $"{item.Type}.{parameter.Name}"))
            .ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void Catalog_UserFacingTextIsAuthoredInEnglish()
    {
        // 英文界面直接透传目录文本（不查表）。18 个节点曾用中文编写，英文界面因此显示中文，
        // 而中文翻译按英文原文索引也无法覆盖它们。此用例锁定“源语言必须是英文”，
        // 中文由 frontend/src/i18n/catalog.ts 覆盖（catalogLocalization.test.ts 反向锁定）。
        var cjk = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var item in BuiltInNodeCatalog.Items)
        {
            if (cjk.IsMatch(item.DisplayName)) offenders.Add($"{item.Type}.displayName");
            if (cjk.IsMatch(item.Description ?? string.Empty)) offenders.Add($"{item.Type}.description");
            foreach (var parameter in item.Parameters)
            {
                if (cjk.IsMatch(parameter.Label)) offenders.Add($"{item.Type}.{parameter.Name}.label");
                if (cjk.IsMatch(parameter.Description ?? string.Empty)) offenders.Add($"{item.Type}.{parameter.Name}.description");
                foreach (var option in parameter.Options ?? [])
                    if (cjk.IsMatch(option.Label)) offenders.Add($"{item.Type}.{parameter.Name}.option={option.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Catalog_ParameterDefaultsAndReferencesAreConsistent()
    {
        // 逐节点审查的规则化版本：默认值必须符合自身声明的类型/范围/选项，所有跨参数引用
        // （可见条件、变体参数、动作条件、资源参数）都必须指向真实存在的参数。
        // 这些规则对 171 个节点全量生效，新增节点不需要再靠人工逐条核对。
        var problems = new List<string>();

        foreach (var item in BuiltInNodeCatalog.Items)
        {
            var names = item.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in item.Parameters)
            {
                var id = $"{item.Type}.{parameter.Name}";
                switch (parameter.Type)
                {
                    case "number":
                        if (parameter.Min is { } min && parameter.Max is { } max && min > max)
                            problems.Add($"{id}: min {min} > max {max}");
                        if (parameter.Step is { } step && step <= 0)
                            problems.Add($"{id}: step {step} must be positive");
                        if (parameter.DefaultValue is { } numericDefault)
                        {
                            if (numericDefault is not (int or long or double or float or decimal))
                                problems.Add($"{id}: numeric default is {numericDefault.GetType().Name}");
                            else
                            {
                                var value = Convert.ToDouble(numericDefault, System.Globalization.CultureInfo.InvariantCulture);
                                if (parameter.Min is { } lower && value < lower)
                                    problems.Add($"{id}: default {value} < min {lower}");
                                if (parameter.Max is { } upper && value > upper)
                                    problems.Add($"{id}: default {value} > max {upper}");
                            }
                        }
                        break;

                    case "boolean":
                        if (parameter.DefaultValue is { } booleanDefault && booleanDefault is not bool)
                            problems.Add($"{id}: boolean default is {booleanDefault.GetType().Name}");
                        break;

                    case "select":
                        if (parameter.Options is null || parameter.Options.Count == 0)
                        {
                            problems.Add($"{id}: select without options");
                        }
                        else if (parameter.DefaultValue is { } selectDefault)
                        {
                            var actual = Convert.ToString(selectDefault, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                            if (!parameter.Options.Any(option => string.Equals(option.Value, actual, StringComparison.OrdinalIgnoreCase)))
                                problems.Add($"{id}: default '{actual}' is not an option");
                        }
                        break;
                }

                var conditions = new List<ParameterVisibility>();
                if (parameter.VisibleWhen is { } singleCondition) conditions.Add(singleCondition);
                if (parameter.VisibleWhenAll is { } allConditions) conditions.AddRange(allConditions);
                foreach (var condition in conditions)
                {
                    if (!names.Contains(condition.Parameter))
                        problems.Add($"{id}: visibility references unknown parameter '{condition.Parameter}'");
                }

                if (parameter.VariantParameter is { } variant && !names.Contains(variant))
                    problems.Add($"{id}: variant parameter '{variant}' does not exist");
            }

            foreach (var action in item.Actions ?? [])
            {
                if (action.WhenParameter is { } condition && !names.Contains(condition))
                    problems.Add($"{item.Type}: action condition references unknown parameter '{condition}'");
            }

            foreach (var resource in item.Resources ?? [])
            {
                if (!names.Contains(resource.Parameter))
                    problems.Add($"{item.Type}: resource references unknown parameter '{resource.Parameter}'");
            }

            foreach (var side in new[] { ("input", item.Inputs), ("output", item.Outputs) })
            {
                var duplicates = side.Item2
                    .GroupBy(port => port.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key);
                foreach (var duplicate in duplicates)
                    problems.Add($"{item.Type}: duplicate {side.Item1} port '{duplicate}'");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Catalog_FrameDeclarationsReferenceExistingPortsAndParameters()
    {
        var problems = new List<string>();

        foreach (var item in BuiltInNodeCatalog.Items.Where(item => item.Frames is not null))
        {
            var inputs = item.Inputs.Select(port => port.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parameters = item.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var rule in item.Frames!)
            {
                var id = $"{item.Type} frame rule {rule.Policy}";
                if (!inputs.Contains(rule.InputPort))
                    problems.Add($"{id}: input port '{rule.InputPort}' does not exist");
                if (rule.Policy == NodeImageFramePolicy.NewFixedFrame && string.IsNullOrWhiteSpace(rule.FrameSuffix))
                    problems.Add($"{id}: NewFixedFrame requires FrameSuffix");
                if (rule.Policy == NodeImageFramePolicy.TransformTarget
                    && !inputs.Contains(rule.TransformPort ?? "transform"))
                    problems.Add($"{id}: TransformTarget requires a transform input port");
                if (rule.Transform == NodeTransformFramePolicy.ReferenceInput && !inputs.Contains(rule.ReferencePort ?? "reference"))
                    problems.Add($"{id}: ReferenceInput requires a reference input port");

                if (string.IsNullOrWhiteSpace(rule.WhenParameter)) continue;
                if (!parameters.TryGetValue(rule.WhenParameter, out var condition))
                {
                    problems.Add($"{id}: condition parameter '{rule.WhenParameter}' does not exist");
                    continue;
                }
                if (rule.WhenValues is not { Count: > 0 })
                {
                    problems.Add($"{id}: conditional rule without WhenValues");
                    continue;
                }
                var options = condition.Options?.Select(option => option.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (options is not null)
                {
                    foreach (var value in rule.WhenValues.Where(value => !options.Contains(value)))
                        problems.Add($"{id}: condition value '{value}' is not an option of '{rule.WhenParameter}'");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Catalog_HasNoRetiredControls()
    {
        var retired = BuiltInNodeCatalog.Items
            .SelectMany(item => item.Parameters
                .Where(parameter => RetiredParameters.Contains(parameter.Name))
                .Select(parameter => $"{item.Type}.{parameter.Name}"))
            .ToArray();
        Assert.Empty(retired);
    }

    [Fact]
    public void Catalog_CrossCuttingControlsAreImplementationBacked()
    {
        Assert.All(BuiltInNodeCatalog.Items.Where(item => item.Parameters.Any(parameter => parameter.Name == "diagnosticsLevel")),
            item => Assert.Contains(item.Type, DiagnosticsLevelTypes));
        Assert.All(BuiltInNodeCatalog.Items.Where(item => item.Parameters.Any(parameter => parameter.Name == "retryPolicy")),
            item => Assert.Contains(item.Type, RetryPolicyTypes));
        Assert.All(BuiltInNodeCatalog.Items.Where(item => item.Parameters.Any(parameter => parameter.Name == "failOnWarning")),
            item => Assert.Equal("flow.try", item.Type));
        Assert.Contains(BuiltInNodeCatalog.Require("flow.try").Parameters, parameter => parameter.Name == "failOnWarning");
    }

    [Fact]
    public void Catalog_VisualOutputsAreBackedByExecutors()
    {
        Assert.All(BuiltInNodeCatalog.Items, item =>
        {
            Assert.DoesNotContain(item.Outputs, port => port.Name == "preview");
            if (item.Outputs.Any(port => port.Name == "overlay"))
                Assert.Contains(item.Type, OverlayProducers);
        });
        Assert.DoesNotContain(BuiltInNodeCatalog.Require("value.arithmetic").Outputs, port => port.Name == "overlay");
        Assert.DoesNotContain(BuiltInNodeCatalog.Require("calibration.detectBoard").Outputs, port => port.Name == "overlay");
        Assert.Contains(BuiltInNodeCatalog.Require("jlvision.imageOp").Outputs, port => port.Name == "overlay");
        Assert.Contains(BuiltInNodeCatalog.Require("inference.segmentation").Outputs, port => port.Name == "overlay");
    }

    [Fact]
    public void Catalog_SnapshotRejectsDuplicateParameters()
    {
        var document = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
        Assert.Equal(BuiltInNodeCatalog.Items.Count, document.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(document.Hash));
    }

    [Fact]
    public void NodeCompletionGuards_ValidatesPolicies()
    {
        var node = new NodeDefinition("t", "flow.delay", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"diagnosticsLevel":"standard","retryPolicy":"none"}""")!);
        Assert.Equal("standard", NodeCompletionGuards.DiagnosticsLevel(node));
        Assert.Equal("none", NodeCompletionGuards.RetryPolicy(node));
        Assert.Equal(1, NodeCompletionGuards.RetryAttempts("none"));
        Assert.Equal(2, NodeCompletionGuards.RetryAttempts("once"));
        Assert.Equal(3, NodeCompletionGuards.RetryAttempts("backoff"));
        var diag = NodeCompletionGuards.BuildDiagnostics("flow.delay", "verbose", 0, 1, ("ok", true));
        Assert.Contains("flow.delay", diag);
        Assert.Contains("ok", diag);
        Assert.Throws<InvalidOperationException>(() =>
            NodeCompletionGuards.DiagnosticsLevel(new NodeDefinition("t", "x", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"diagnosticsLevel":"nope"}""")!)));
    }
}
