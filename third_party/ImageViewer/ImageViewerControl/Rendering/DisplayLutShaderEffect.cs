using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ImageViewer.Services;

namespace ImageViewer.Rendering
{
    /// <summary>
    /// 查找表索引方式（着色器常量 c0）。
    /// Chinese: 三个取值让着色器只有一条"读表"实现：透传不读表、伪彩按强度读、无调色板按逐通道读。
    /// </summary>
    internal enum DisplayLutMode
    {
        /// <summary>原样输出像素。</summary>
        PassThrough = 0,

        /// <summary>按强度索引读表（伪彩色路径）。</summary>
        IntensityIndexed = 1,

        /// <summary>按每个颜色通道分别索引读表（无色板路径，保留彩色）。</summary>
        PerChannelIndexed = 2
    }

    internal sealed class DisplayLutShaderEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(DisplayLutShaderEffect), 0);

        /// <summary>
        /// 显示查找表（256×1 位图，s1）。
        /// Chinese: 与 CPU 路径共用同一张表——着色器既不带调色板公式，也不带传递函数，两条路径不可能再漂移。
        /// English: The very same table the CPU path uses; the shader carries neither palette ramps nor the transfer curve.
        /// </summary>
        public static readonly DependencyProperty DisplayLutProperty = RegisterPixelShaderSamplerProperty(nameof(DisplayLut), typeof(DisplayLutShaderEffect), 1);

        public static readonly DependencyProperty LutModeProperty = DependencyProperty.Register(
            nameof(LutMode),
            typeof(double),
            typeof(DisplayLutShaderEffect),
            new UIPropertyMetadata((double)DisplayLutMode.PassThrough, PixelShaderConstantCallback(0)));

        public DisplayLutShaderEffect(ImageViewerDisplaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            PixelShader = DisplayLutShaderCompiler.GetPixelShader();
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(DisplayLutProperty);
            UpdateShaderValue(LutModeProperty);
            DisplayLut = CreateDisplayLutBrush(settings);
            LutMode = (double)GetMode(settings);
        }

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public Brush DisplayLut
        {
            get => (Brush)GetValue(DisplayLutProperty);
            set => SetValue(DisplayLutProperty, value);
        }

        public double LutMode
        {
            get => (double)GetValue(LutModeProperty);
            set => SetValue(LutModeProperty, value);
        }

        private static DisplayLutMode GetMode(ImageViewerDisplaySettings settings) =>
            settings.IsPassThrough ? DisplayLutMode.PassThrough
            : settings.UsesPerChannelIndex ? DisplayLutMode.PerChannelIndexed
            : DisplayLutMode.IntensityIndexed;

        /// <summary>
        /// 把共用的查找表包成采样输入。
        /// Chinese: 用 ImageBrush 直接映射整张 256×1 位图（不设 Stretch 偏移），避免任何额外的坐标变换影响取样位置。
        /// English: Wraps the shared table in an ImageBrush with a straight 1:1 mapping so sampling lands on the intended texel.
        /// </summary>
        private static Brush CreateDisplayLutBrush(ImageViewerDisplaySettings settings)
        {
            var brush = new ImageBrush(ImageViewerDisplaySourceService.CreateDisplayLutBitmap(settings))
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 256, 1),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 256, 1)
            };

            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }
    }
}
