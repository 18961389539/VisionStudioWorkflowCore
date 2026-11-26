using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Wpf.Controls.ImageViewer;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>
/// 参数编辑项 ViewModel：按 <see cref="ParameterDescriptor.Type"/> 驱动详情窗口里的动态表单控件。
/// </summary>
public sealed partial class NodeParameterItemViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions RoiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public NodeParameterItemViewModel(ParameterDescriptor descriptor, JsonElement? rawValue)
    {
        Descriptor = descriptor;
        Name = descriptor.Name;
        Label = string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.Name : descriptor.Label;
        Type = descriptor.Type;
        Unit = descriptor.Unit ?? string.Empty;
        Description = descriptor.Description ?? string.Empty;
        Group = string.IsNullOrWhiteSpace(descriptor.Group) ? "常规参数" : descriptor.Group;
        Options = descriptor.Options ?? [];

        IsBoolean = Type.Equals("boolean", StringComparison.OrdinalIgnoreCase);
        IsRoi = Type.Equals("roi", StringComparison.OrdinalIgnoreCase);
        IsSelect = Options.Count > 0 && !IsBoolean && !IsRoi;
        IsMultiline = Type.Equals("code", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("script", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("json", StringComparison.OrdinalIgnoreCase);

        InitializeValue(rawValue, descriptor.DefaultValue);
    }

    public ParameterDescriptor Descriptor { get; }
    public string Name { get; }
    public string Label { get; }
    public string Type { get; }
    public string Unit { get; }
    public string Description { get; }
    public string Group { get; }
    public IReadOnlyList<ParameterOption> Options { get; }
    public bool IsBoolean { get; }
    public bool IsRoi { get; }
    public bool IsSelect { get; }
    public bool IsMultiline { get; }
    public bool IsText => !IsBoolean && !IsSelect && !IsRoi;
    public string RoiSummaryText => FormatRoiSummary(TryGetRoi());
    public bool IsDirectory => Type.Equals("directory", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("directory", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("folder", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("directoryPath", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("folderPath", StringComparison.OrdinalIgnoreCase);
    public bool IsPath => Type.Equals("file", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("path", StringComparison.OrdinalIgnoreCase)
        || IsDirectory
        || Name.EndsWith("path", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("file", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("fileName", StringComparison.OrdinalIgnoreCase);

    public bool IsRequired => !Descriptor.Optional && !IsBoolean;
    public string RangeHint
    {
        get
        {
            if (Descriptor.Min is null && Descriptor.Max is null) return string.Empty;
            var min = Descriptor.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞";
            var max = Descriptor.Max?.ToString(CultureInfo.InvariantCulture) ?? "+∞";
            return $"范围 [{min}, {max}]";
        }
    }

    [ObservableProperty]
    private bool _isActive = true;

    [RelayCommand]
    private void BrowsePath()
    {
        if (IsDirectory)
        {
            var folder = new OpenFolderDialog();
            if (folder.ShowDialog() == true) TextValue = folder.FolderName;
            return;
        }
        var dialog = new OpenFileDialog { CheckFileExists = false, FileName = TextValue };
        if (dialog.ShowDialog() == true)
        {
            TextValue = dialog.FileName;
        }
    }

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private string _textValue = string.Empty;

    partial void OnTextValueChanged(string value)
    {
        if (IsRoi) OnPropertyChanged(nameof(RoiSummaryText));
    }

    public VisionRoi? TryGetRoi()
    {
        if (!IsRoi || string.IsNullOrWhiteSpace(TextValue)) return null;
        try
        {
            return JsonSerializer.Deserialize<VisionRoi>(TextValue, RoiJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SetRoi(VisionRoi? roi)
    {
        TextValue = roi is null ? string.Empty : JsonSerializer.Serialize(roi, RoiJsonOptions);
        OnPropertyChanged(nameof(RoiSummaryText));
    }

    private static string FormatRoiSummary(VisionRoi? roi) => roi switch
    {
        null => "未设置（使用全图或上游 roi 端口）",
        { Type: VisionRoiType.Rectangle } => $"矩形 (X={roi.X:0.#}, Y={roi.Y:0.#}, W={roi.Width:0.#}, H={roi.Height:0.#})",
        { Type: VisionRoiType.Circle } => $"圆形 (X={roi.X:0.#}, Y={roi.Y:0.#}, R={roi.Radius:0.#})",
        { Type: VisionRoiType.Polygon } => $"多边形 ({roi.Points?.Count ?? 0} 个顶点)",
        { Type: VisionRoiType.Brush } => $"笔刷 ({roi.Strokes?.Count ?? 0} 条笔迹)",
        _ => "自定义 ROI"
    };

    private void InitializeValue(JsonElement? rawValue, object? defaultValue)
    {
        if (rawValue is { } el && el.ValueKind != JsonValueKind.Undefined && el.ValueKind != JsonValueKind.Null)
        {
            if (IsBoolean)
            {
                BoolValue = el.ValueKind == JsonValueKind.True
                    || (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b) && b);
                TextValue = BoolValue ? "true" : "false";
                return;
            }

            TextValue = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.GetRawText();
            return;
        }

        if (defaultValue is bool defBool)
        {
            BoolValue = defBool;
            TextValue = defBool ? "true" : "false";
        }
        else
        {
            TextValue = Convert.ToString(defaultValue, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>把当前用户输入序列化回后端约定的 <see cref="JsonElement"/>。</summary>
    public bool TryBuildJsonElement(out JsonElement element, out string? error)
    {
        error = null;
        element = default;
        if (IsBoolean)
        {
            element = JsonSerializer.SerializeToElement(BoolValue);
            return true;
        }

        if (IsRoi)
        {
            if (string.IsNullOrWhiteSpace(TextValue))
            {
                return true;
            }
            try
            {
                var roi = JsonSerializer.Deserialize<VisionRoi>(TextValue, RoiJsonOptions);
                if (roi is null)
                {
                    error = $"参数“{Label}”ROI 内容无效";
                    return false;
                }
                element = JsonSerializer.SerializeToElement(roi, RoiJsonOptions);
                return true;
            }
            catch (JsonException ex)
            {
                error = $"参数“{Label}”ROI JSON 格式错误: {ex.Message}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(TextValue) && Descriptor.Optional)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(TextValue))
        {
            error = $"参数“{Label}”不能为空";
            return false;
        }
        if (IsSelect && !Options.Any(option => option.Value == TextValue))
        {
            error = $"参数“{Label}”需要选择有效选项";
            return false;
        }
        if (Type.Equals("number", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("double", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(TextValue) && Descriptor.Optional)
            {
                element = default;
                return true;
            }
            if (!double.TryParse(TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                || !double.IsFinite(num))
            {
                element = default;
                error = $"参数“{Label}”需要有效数字";
                return false;
            }
            if ((Descriptor.Min is { } min && num < min) || (Descriptor.Max is { } max && num > max))
            {
                error = $"参数“{Label}”超出范围 [{Descriptor.Min}, {Descriptor.Max}]";
                return false;
            }
            if (Descriptor.Integer)
            {
                if (!long.TryParse(TextValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    error = $"参数“{Label}”需要整数，不能自动取整";
                    return false;
                }
                element = JsonSerializer.SerializeToElement(integer);
            }
            else
            {
                element = JsonSerializer.SerializeToElement(num);
            }
            return true;
        }

        if (Type.Equals("integer", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("int", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(TextValue) && Descriptor.Optional)
            {
                element = default;
                return true;
            }
            if (!long.TryParse(TextValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                element = default;
                error = $"参数“{Label}”需要整数";
                return false;
            }
            if ((Descriptor.Min is { } min && intVal < min) || (Descriptor.Max is { } max && intVal > max))
            {
                error = $"参数“{Label}”超出范围 [{Descriptor.Min}, {Descriptor.Max}]";
                return false;
            }
            element = JsonSerializer.SerializeToElement(intVal);
            return true;
        }

        if (Type.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(TextValue);
                element = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException ex)
            {
                element = default;
                error = $"参数“{Label}”JSON 格式错误: {ex.Message}";
                return false;
            }
        }

        element = JsonSerializer.SerializeToElement(TextValue);
        return true;
    }

    public void ResetToDefault()
    {
        InitializeValue(null, Descriptor.DefaultValue);
    }
}

/// <summary>端口及最近执行数值快照展示项。</summary>
public sealed record PortValueDisplayItem(
    string Direction,
    string PortName,
    string DataType,
    bool Required,
    string LatestValue);

/// <summary>
/// 双击节点弹出的详情窗口 ViewModel：集中承载参数配置、端口契约、运行报告与节点图像预览。
/// </summary>
public sealed partial class NodeDetailViewModel : ObservableObject
{
    private readonly WorkflowNodeViewModel _node;
    private readonly Action<BitmapSource, string>? _sendToMainViewer;
    private readonly Func<VisionRoi?>? _getViewerRoi;
    private readonly Action<VisionRoi>? _previewViewerRoi;

    public NodeDetailViewModel(
        WorkflowNodeViewModel node,
        IReadOnlyList<RunNodeImage>? nodeImages = null,
        Action<BitmapSource, string>? sendToMainViewer = null,
        Func<VisionRoi?>? getViewerRoi = null,
        Action<VisionRoi>? previewViewerRoi = null)
    {
        _node = node;
        _sendToMainViewer = sendToMainViewer;
        _getViewerRoi = getViewerRoi;
        _previewViewerRoi = previewViewerRoi;
        _nodeTitle = node.Title;

        if (node.Catalog is not null)
        {
            foreach (var p in node.Catalog.Parameters.Where(x => !x.Internal || x.Type.Equals("roi", StringComparison.OrdinalIgnoreCase)))
            {
                node.Parameters.TryGetValue(p.Name, out var raw);
                Parameters.Add(new NodeParameterItemViewModel(p, raw));
            }
        }
        var view = CollectionViewSource.GetDefaultView(Parameters);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NodeParameterItemViewModel.Group)));
        view.Filter = MatchesParameterFilter;
        GroupedParameters = view;
        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(NodeParameterItemViewModel.TextValue) or nameof(NodeParameterItemViewModel.BoolValue))
                    RefreshParameterVisibility();
            };
        }
        RefreshParameterVisibility();

        var report = node.LastReport;
        foreach (var input in node.Inputs)
        {
            var val = report?.Inputs is not null && report.Inputs.TryGetValue(input.PortName, out var snap)
                ? snap.Display
                : "-";
            Ports.Add(new PortValueDisplayItem("输入", input.PortName, input.TypeDisplay, input.Required, val));
        }
        foreach (var output in node.Outputs)
        {
            var val = report?.Outputs is not null && report.Outputs.TryGetValue(output.PortName, out var snap)
                ? snap.Display
                : "-";
            Ports.Add(new PortValueDisplayItem("输出", output.PortName, output.TypeDisplay, output.Required, val));
        }

        if (nodeImages is not null)
        {
            var first = nodeImages.FirstOrDefault(img => img.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
            if (first is not null)
            {
                NodePreviewImage = VisionImageInterop.FromEncodedBytes(first.Bytes);
                NodePreviewCaption = $"{node.Title} · 端口 {first.PortName} ({first.Width}×{first.Height})";
            }
        }
    }

    public string NodeId => _node.Id;
    public string NodeType => _node.Type;
    public string CategoryZh => _node.CategoryZh;
    public bool IsNonStableMaturity => _node.IsNonStableMaturity;
    public string MaturityBadge => _node.Maturity switch
    {
        NodeMaturity.Preview => "Preview（近似/子集能力）",
        NodeMaturity.Experimental => "Experimental（实验节点）",
        _ => "Stable"
    };
    public bool HasViewerRoiBridge => _getViewerRoi is not null;
    public string Description => _node.Catalog?.Description ?? "暂无节点说明。";
    public string StatusSummary => _node.LastReport is { } r
        ? (r.Success ? $"执行成功，耗时 {r.DurationMs:0.00} ms" : $"执行失败：{r.Error}")
        : "尚未运行";
    public string ExecutionStateKey => _node.LastReport is { } r
        ? (r.Success ? "Succeeded" : "Failed")
        : "Idle";

    public ObservableCollection<NodeParameterItemViewModel> Parameters { get; } = [];
    public ICollectionView GroupedParameters { get; }
    public ObservableCollection<PortValueDisplayItem> Ports { get; } = [];

    [RelayCommand]
    private void ImportRoiFromViewer(NodeParameterItemViewModel? item)
    {
        if (item is null) return;
        var roi = _getViewerRoi?.Invoke();
        if (roi is null)
        {
            ValidationError = "主图像区尚无可用的用户 ROI。请先在右侧图像区点击“框选ROI / 圆ROI / 多边形ROI”绘制区域。";
            return;
        }
        item.SetRoi(roi);
        ValidationError = null;
    }

    [RelayCommand]
    private void PreviewRoiInViewer(NodeParameterItemViewModel? item)
    {
        if (item is null) return;
        var roi = item.TryGetRoi();
        if (roi is null)
        {
            ValidationError = $"参数“{item.Label}”尚未设置有效的 ROI。";
            return;
        }
        _previewViewerRoi?.Invoke(roi);
        ValidationError = null;
    }

    [RelayCommand]
    private void ClearRoi(NodeParameterItemViewModel? item)
    {
        if (item is null) return;
        item.SetRoi(null);
        ValidationError = null;
    }

    [ObservableProperty]
    private string _nodeTitle;

    [ObservableProperty]
    private string _parameterFilter = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private BitmapSource? _nodePreviewImage;

    [ObservableProperty]
    private string _nodePreviewCaption = "该节点尚无图像输出";

    public bool HasPreviewImage => NodePreviewImage is not null;
    public int ActiveParameterCount => Parameters.Count(p => p.IsActive);
    public int VisibleParameterCount => Parameters.Count(MatchesParameterFilter);
    public bool HasNoVisibleParameters => VisibleParameterCount == 0;

    partial void OnParameterFilterChanged(string value)
    {
        GroupedParameters.Refresh();
        NotifyParameterCounts();
    }

    [RelayCommand]
    private void ClearParameterFilter() => ParameterFilter = string.Empty;

    private bool MatchesParameterFilter(object item)
    {
        if (item is not NodeParameterItemViewModel p || !p.IsActive) return false;
        var kw = ParameterFilter.Trim();
        if (string.IsNullOrEmpty(kw)) return true;
        return p.Label.Contains(kw, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
            || p.Group.Contains(kw, StringComparison.OrdinalIgnoreCase)
            || p.Description.Contains(kw, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyParameterCounts()
    {
        OnPropertyChanged(nameof(ActiveParameterCount));
        OnPropertyChanged(nameof(VisibleParameterCount));
        OnPropertyChanged(nameof(HasNoVisibleParameters));
    }

    private void RefreshParameterVisibility()
    {
        if (_node.Catalog is not { } catalog)
        {
            NotifyParameterCounts();
            return;
        }
        foreach (var parameter in Parameters)
        {
            var descriptor = parameter.Descriptor;
            var conditions = (descriptor.VisibleWhen is { } condition ? new[] { condition } : [])
                .Concat(descriptor.VisibleWhenAll ?? []);
            parameter.IsActive = !(_node.Inputs.Any(port => port.IsConnected
                && descriptor.OverriddenByPorts?.Contains(port.PortName, StringComparer.OrdinalIgnoreCase) == true))
                && conditions.All(visibility =>
                {
                    var dependency = Parameters.FirstOrDefault(item => item.Name.Equals(visibility.Parameter, StringComparison.OrdinalIgnoreCase));
                    var value = dependency is null
                        ? NodeParameterValue.Resolve(new NodeDefinition(_node.Id, _node.Type, _node.Title, null, _node.Parameters),
                            catalog, visibility.Parameter)
                        : dependency.IsBoolean ? (dependency.BoolValue ? "true" : "false") : dependency.TextValue;
                    return visibility.Values.Any(expected => string.Equals(expected, value, StringComparison.OrdinalIgnoreCase));
                });
        }
        GroupedParameters.Refresh();
        NotifyParameterCounts();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (var p in Parameters)
        {
            p.ResetToDefault();
        }
        ValidationError = null;
    }

    [RelayCommand]
    private void PushToMainViewer()
    {
        if (NodePreviewImage is not null)
        {
            _sendToMainViewer?.Invoke(NodePreviewImage, NodePreviewCaption);
        }
    }

    /// <summary>校验并应用编辑后的节点名称和参数回写到画布节点。</summary>
    public bool TryApplyChanges()
    {
        var staged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parameters.Where(parameter => parameter.IsActive))
        {
            if (!p.TryBuildJsonElement(out var element, out var error))
            {
                ValidationError = error;
                return false;
            }
            if (element.ValueKind != JsonValueKind.Undefined)
            {
                staged[p.Name] = element;
            }
        }

        _node.Title = string.IsNullOrWhiteSpace(NodeTitle) ? _node.DefaultDisplayName : NodeTitle.Trim();
        foreach (var p in Parameters.Where(parameter => parameter.IsActive))
        {
            if (staged.TryGetValue(p.Name, out var element))
            {
                _node.Parameters[p.Name] = element;
            }
            else
            {
                _node.Parameters.Remove(p.Name);
            }
        }

        ValidationError = null;
        return true;
    }
}
