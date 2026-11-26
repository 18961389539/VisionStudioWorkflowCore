using System;
using System.Text.Json.Serialization;

namespace ImageViewer.Core.Display;

/// <summary>
/// 显示传递函数参数：窗宽/窗位、亮度、对比度、伽马与反色。
/// Chinese: 参数一律定义在**归一化显示域 [0,1]**上，与渲染后端无关——CPU 分块路径和 Shader 路径共用由
/// <see cref="DisplayTransfer"/> 生成的同一张 256 级 LUT，所以显示调节不存在"两条路径各算一套"的漂移。
/// 之所以是"显示域"而不是"原始码域"：源像素在进入 LUT 之前已经量化成 256 级显示码（Gray16 按 v/257），
/// 与伪彩色走的是同一条量化路径。代价是极窄窗口的可用级数受该量化限制，这是换取两条路径一致的直接结果。
/// 运算顺序固定为 窗宽窗位 → 亮度 → 对比度 → 伽马 → 反色；每一步都截断到 [0,1]。
/// English: All parameters live in the normalized display domain and both render paths sample the very same table.
/// </summary>
public sealed record DisplayAdjustment
{
    /// <summary>不改变任何像素的默认参数。</summary>
    public static DisplayAdjustment Identity { get; } = new();

    /// <summary>
    /// 窗宽（归一化强度跨度）。必须为正；1 表示覆盖整个显示范围。
    /// Chinese: 窗宽小于 1 会把该区间拉伸到全屏、区间外截断，就是常规"窗位窗宽"效果。
    /// </summary>
    public double WindowWidth { get; init; } = 1.0;

    /// <summary>窗位（窗口中心，归一化），取值 [0,1]；0.5 表示窗口居中。</summary>
    public double WindowLevel { get; init; } = 0.5;

    /// <summary>亮度：归一化加法量，取值 [-1,1]；0 为不变。</summary>
    public double Brightness { get; init; }

    /// <summary>对比度：围绕中灰的增益量，取值 [-1,1]；0 为不变，增益为 1 + 本值。</summary>
    public double Contrast { get; init; }

    /// <summary>
    /// 伽马：输出 = 输入^(1/Gamma)，必须为正；1 为线性。
    /// Chinese: Gamma 大于 1 提亮中间调（显示编码伽马），小于 1 压暗中间调。
    /// </summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>反色：在全部其它变换之后执行，因此窗位窗口方向也随之翻转。</summary>
    public bool Invert { get; init; }

    /// <summary>
    /// 是否逐项等于默认参数。
    /// Chinese: 为 true 时渲染路径直接透传像素——既不生成 LUT，Shader 也不采样，未调节时零额外开销。
    /// 派生值，不参与会话文件的读写。
    /// </summary>
    [JsonIgnore]
    public bool IsIdentity => this == Identity;

    /// <summary>
    /// 校验参数落在各自定义域内，非法值抛 <see cref="ArgumentOutOfRangeException"/>。
    /// Chinese: 判据只有一份（<see cref="TryValidate"/>），依赖属性那种"不能抛异常"的校验入口读同一份。
    /// </summary>
    public void Validate()
    {
        if (!TryValidate(out string? error))
        {
            throw new ArgumentOutOfRangeException(nameof(WindowWidth), this, error);
        }
    }

    /// <summary>
    /// 尝试校验参数。
    /// Chinese: 返回 false 时 <paramref name="error"/> 指出第一个越界的参数——窗宽与伽马只要求正的有限数，
    /// 窗位在 [0,1]，亮度与对比度在 [-1,1]。
    /// </summary>
    /// <param name="error">非法原因；合法时为 null。</param>
    /// <returns>参数是否全部合法。</returns>
    public bool TryValidate(out string? error)
    {
        error = null;
        if (!IsPositiveFinite(WindowWidth))
        {
            error = $"{nameof(WindowWidth)} must be a positive, finite number.";
        }
        else if (!IsUnitRange(WindowLevel))
        {
            error = $"{nameof(WindowLevel)} must be between 0 and 1.";
        }
        else if (!IsNormalizedRange(Brightness))
        {
            error = $"{nameof(Brightness)} must be between -1 and 1.";
        }
        else if (!IsNormalizedRange(Contrast))
        {
            error = $"{nameof(Contrast)} must be between -1 and 1.";
        }
        else if (!IsPositiveFinite(Gamma))
        {
            error = $"{nameof(Gamma)} must be a positive, finite number.";
        }

        return error == null;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static bool IsUnitRange(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

    private static bool IsNormalizedRange(double value) => double.IsFinite(value) && value >= -1 && value <= 1;

    /// <summary>
    /// 求单个归一化输入的处理结果。
    /// Chinese: 输入与输出都截断到 [0,1]，因此可以安全地用于 8 位显示码（input = code / 255）。
    /// </summary>
    /// <param name="input">归一化输入强度。</param>
    /// <returns>归一化输出强度。</returns>
    public double Evaluate(double input)
    {
        double value = ClampUnit(input);
        double halfWidth = WindowWidth / 2.0;
        value = ClampUnit((value - (WindowLevel - halfWidth)) / WindowWidth);
        value = ClampUnit(value + Brightness);
        value = ClampUnit((value - 0.5) * (1.0 + Contrast) + 0.5);
        if (Gamma != 1.0 && value > 0.0)
        {
            value = ClampUnit(Math.Pow(value, 1.0 / Gamma));
        }

        return Invert ? 1.0 - value : value;
    }

    private static double ClampUnit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    /// <summary>
    /// 单行摘要（渲染状态、日志与报告用）。
    /// Chinese: 固定用不变文化输出，避免同一份设置在中文环境里出现全角小数点之类的不一致。
    /// </summary>
    public override string ToString()
    {
        if (IsIdentity)
        {
            return "none";
        }

        return FormattableString.Invariant(
            $"W={WindowWidth:0.###}, L={WindowLevel:0.###}, B={Brightness:+0.###;-0.###;0}, C={Contrast:+0.###;-0.###;0}, Gamma={Gamma:0.###}{(Invert ? ", invert" : string.Empty)}");
    }
}
