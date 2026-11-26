using ImageViewer.Core.Display;

namespace ImageViewer.Services
{
    /// <summary>
    /// "屏幕上看到什么"的完整描述：调色板 + 显示调节。
    /// Chinese: 合成一个类型是为了让它作为**一个整体**进入渲染缓存键——分项传递迟早会出现"某个路径忘了带上调节"
    /// 的漏缓存/错缓存。它也是 CPU 与 Shader 共用同一张 LUT 的输入：两者只从这一份数据派生取值表。
    /// English: One value so the tile cache key cannot half-apply it, and so both render paths derive their LUT from the
    /// same input.
    /// </summary>
    public sealed record ImageViewerDisplaySettings
    {
        /// <summary>逐项等于默认参数：像素原样透传，不生成也不采样 LUT。</summary>
        public static ImageViewerDisplaySettings PassThrough { get; } = new(PseudoColorPalette.None, DisplayAdjustment.Identity);

        /// <summary>构造函数。</summary>
        /// <param name="palette">伪彩调色板。</param>
        /// <param name="adjustment">显示调节参数；null 按默认参数处理。</param>
        public ImageViewerDisplaySettings(PseudoColorPalette palette, DisplayAdjustment? adjustment = null)
        {
            Palette = palette;
            Adjustment = adjustment ?? DisplayAdjustment.Identity;
        }

        /// <summary>伪彩调色板。</summary>
        public PseudoColorPalette Palette { get; }

        /// <summary>显示调节参数（窗宽窗位/亮度/对比度/伽马/反色）。</summary>
        public DisplayAdjustment Adjustment { get; }

        /// <summary>是否完全不需要显示处理。</summary>
        public bool IsPassThrough => Palette == PseudoColorPalette.None && Adjustment.IsIdentity;

        /// <summary>
        /// 查找表按哪种方式索引。
        /// Chinese: 启用调色板时按**强度**索引（伪彩本来就是单通道映射）；未启用时按**逐通道**索引，
        /// 这样"只调亮度/窗宽窗位"不会把彩色图变成灰度。CPU 与 Shader 都读这一个判据，不会各选各的。
        /// </summary>
        public bool UsesPerChannelIndex => Palette == PseudoColorPalette.None;

        /// <summary>校验调色板取值范围与调节参数定义域。</summary>
        public void Validate()
        {
            if (!Enum.IsDefined(Palette))
            {
                throw new ArgumentOutOfRangeException(nameof(Palette), Palette, "Unknown pseudo-color palette.");
            }

            Adjustment.Validate();
        }
    }
}
