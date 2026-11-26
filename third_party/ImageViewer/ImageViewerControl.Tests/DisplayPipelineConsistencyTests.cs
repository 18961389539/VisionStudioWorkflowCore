using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using ImageViewer.Core.Display;
using ImageViewer.Rendering;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// CPU / GPU 显示通路一致性守卫。
    /// Chinese: 两条路径必须共享同一套定义——强度权重、alpha 策略、显示传递曲线与调色板取值。
    /// 早先 CPU 依赖 WPF 的 Gray8 转换（权重不可控）、alpha 固定 255、调色板各自实现（hot/jet 曲线不同），
    /// 因此在没有 GPU 的机器上颜色会不一样。现在两边都只采样 <see cref="ImageViewerDisplaySourceService.GetDisplayLut"/>
    /// 生成的同一张 256 项表，着色器既不带调色板公式也不带传递函数；这些用例把"共享定义"钉住。
    /// English: Guards that the CPU fallback and the shader share one definition — intensity weights, alpha policy,
    /// transfer curve and palette values — so the same document renders identically with and without a usable shader.
    /// </summary>
    public class DisplayPipelineConsistencyTests
    {
        private static readonly ImageViewerDisplaySettings Hot = new(PseudoColorPalette.Hot);

        [Fact]
        public void ApplyDisplayLut_PreservesInputAlpha()
        {
            // Bgra32 的半透明像素（BGRA 字节序）：alpha 必须保留（与 shader 的 float4(color, inputColor.a) 一致）。
            byte[] translucent = ReadOutputPixel(CreateBitmap(1, 1, [0, 0, 255, 128]));
            Assert.Equal(128, translucent[3]);

            byte[] opaque = ReadOutputPixel(CreateBitmap(1, 1, [0, 0, 255, 255]));
            Assert.Equal(255, opaque[3]);
        }

        [Fact]
        public void ApplyDisplayLut_UnpremultipliesPbgra32BeforeIndexingTheLut()
        {
            // 半透明纯红：Pbgra32 存储的是 R=128（预乘），直通输入是 R=255。
            // 两条路径进入 LUT 之前必须得到同一个直通颜色，否则半透明图像会变暗/偏色。
            var settings = new ImageViewerDisplaySettings(PseudoColorPalette.None, DisplayAdjustment.Identity with { Invert = true });
            byte[] premultiplied = ReadOutputPixel(
                CreateBitmap(1, 1, PixelFormats.Pbgra32, [0, 0, 128, 128]), settings);
            byte[] straight = ReadOutputPixel(
                CreateBitmap(1, 1, PixelFormats.Bgra32, [0, 0, 255, 128]), settings);

            Assert.Equal(straight, premultiplied);
            Assert.Equal(128, premultiplied[3]);
        }

        [Fact]
        public void Shader_UnpremultipliesInputAndPremultipliesOutput()
        {
            string source = ReadEmbeddedShaderSource();

            // WPF ShaderEffect 的输入/输出都按预乘 alpha 处理；缺了这两步，半透明边缘会被重复乘 alpha。
            Assert.Contains("inputColor.rgb / alpha", source, StringComparison.Ordinal);
            Assert.Contains("float4(color * alpha, alpha)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void ApplyDisplayLut_WithoutAlphaChannel_TreatsInputAsOpaque()
        {
            byte[] color = ReadOutputPixel(CreateBitmap(1, 1, PixelFormats.Bgr24, [255, 0, 0]));
            Assert.Equal(255, color[3]);

            byte[] gray = ReadOutputPixel(CreateBitmap(1, 1, PixelFormats.Gray8, [200]));
            Assert.Equal(255, gray[3]);
        }

        [Fact]
        public void ApplyDisplayLut_UsesTheSameLuminanceWeightsAsTheShader()
        {
            // 纯红：强度 = round(0.299 * 255) = 76 —— 与 shader 的 dot(rgb, 0.299/0.587/0.114) 同一组权重。
            byte[] lut = ImageViewerDisplaySourceService.GetDisplayLut(Hot);
            byte[] actual = ReadOutputPixel(CreateBitmap(1, 1, [0, 0, 255, 255]));
            int lutOffset = 76 * 4;

            Assert.Equal(lut[lutOffset], actual[0]);
            Assert.Equal(lut[lutOffset + 1], actual[1]);
            Assert.Equal(lut[lutOffset + 2], actual[2]);
        }

        [Fact]
        public void ApplyDisplayLut_MapsGray16WithTheSameRangeAsTheShader()
        {
            byte[] lut = ImageViewerDisplaySourceService.GetDisplayLut(Hot);

            // 65535 → 255，257 → 1（v/257），0 → 0；与 shader 的 intensity * 255（intensity = v/65535）一致。
            Assert.Equal(lut[255 * 4], ReadOutputPixel(CreateGray16(65535))[0]);
            Assert.Equal(lut[1 * 4], ReadOutputPixel(CreateGray16(257))[0]);
            Assert.Equal(lut[0], ReadOutputPixel(CreateGray16(0))[0]);
        }

        [Fact]
        public void ApplyDisplayLut_PassThroughReturnsTheSourceUnchanged()
        {
            System.Windows.Media.Imaging.BitmapSource source = CreateBitmap(1, 1, [10, 20, 30, 255]);

            Assert.Same(source, ImageViewerDisplaySourceService.ApplyDisplayLut(source, ImageViewerDisplaySettings.PassThrough));
            Assert.Null(ImageViewerDisplaySourceService.CreateDisplayEffect(ImageViewerDisplaySettings.PassThrough));
        }

        [Fact]
        public void ApplyDisplayLut_WithoutPalette_KeepsColorChannelsInsteadOfDesaturating()
        {
            // 无色板时按逐通道索引：蓝像素反色后应变成黄色，而不是灰度。
            var settings = new ImageViewerDisplaySettings(PseudoColorPalette.None, DisplayAdjustment.Identity with { Invert = true });
            byte[] actual = ReadOutputPixel(CreateBitmap(1, 1, [255, 0, 0, 255]), settings);

            Assert.Equal(0, actual[0]);
            Assert.Equal(255, actual[1]);
            Assert.Equal(255, actual[2]);
        }

        [Fact]
        public void ApplyDisplayLut_AppliesWindowLevelFromTheSharedTransferCurve()
        {
            // 窗宽 0.5、窗位 0.5：窗口外的 0/255 截断到两端，中灰留在中灰附近。
            var adjustment = DisplayAdjustment.Identity with { WindowWidth = 0.5, WindowLevel = 0.5 };
            var settings = new ImageViewerDisplaySettings(PseudoColorPalette.None, adjustment);
            byte[] lut = ImageViewerDisplaySourceService.GetDisplayLut(settings);

            Assert.Equal(0, lut[0]);
            Assert.Equal(255, lut[255 * 4]);
            Assert.InRange(lut[128 * 4], 127, 129);
        }

        [Fact]
        public void Shader_SamplesTheSharedLutInsteadOfCarryingTransferOrPaletteFormulas()
        {
            string source = ReadEmbeddedShaderSource();

            // 着色器不再自带调色板公式，也不再自带窗宽窗位/亮度/对比度/伽马曲线。
            Assert.DoesNotContain("hotPalette", source, StringComparison.Ordinal);
            Assert.DoesNotContain("jetPalette", source, StringComparison.Ordinal);
            Assert.DoesNotContain("viridisPalette", source, StringComparison.Ordinal);
            Assert.DoesNotContain("pow(", source, StringComparison.Ordinal);
            Assert.Contains("sampler2D DisplayLut : register(s1)", source, StringComparison.Ordinal);
            Assert.Contains("tex2D(DisplayLut", source, StringComparison.Ordinal);

            // 权重必须与 ImageViewerPixelAccess.ReadIntensity 一致（上面那条纯红用例把 CPU 侧钉住了）。
            Assert.Contains("0.299, 0.587, 0.114", source, StringComparison.Ordinal);

            // 保留输入 alpha。
            Assert.Contains("inputColor.a", source, StringComparison.Ordinal);
        }

        [Fact]
        public void DisplayLutBitmap_HasOneEntryPerIntensityLevel()
        {
            System.Windows.Media.Imaging.BitmapSource lut = ImageViewerDisplaySourceService.CreateDisplayLutBitmap(new ImageViewerDisplaySettings(PseudoColorPalette.Viridis));

            Assert.Equal(256, lut.PixelWidth);
            Assert.Equal(1, lut.PixelHeight);
            Assert.True(lut.IsFrozen);
        }

        [Fact]
        public void CreateDisplayEffect_WithLutSampler_CompilesAndBuildsForEverySetting()
        {
            // d3dcompiler_47.dll 在少数环境缺失：那属于"没有 GPU 路径"，跳过而不是误报失败。
            try
            {
                _ = DisplayLutShaderCompiler.GetPixelShader();
            }
            catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
            {
                return;
            }

            foreach (PseudoColorPalette palette in new[]
            {
                PseudoColorPalette.Hot,
                PseudoColorPalette.Jet,
                PseudoColorPalette.Viridis
            })
            {
                System.Windows.Media.Effects.Effect? effect = ImageViewerDisplaySourceService.CreateDisplayEffect(new ImageViewerDisplaySettings(palette));

                var shaderEffect = Assert.IsType<DisplayLutShaderEffect>(effect);
                Assert.NotNull(shaderEffect.DisplayLut);
                Assert.Equal((double)DisplayLutMode.IntensityIndexed, shaderEffect.LutMode);
            }

            // 无色板但带调节 → 逐通道采样（保住彩色）；着色器与 CPU 读同一个判据。
            System.Windows.Media.Effects.Effect? adjustable = ImageViewerDisplaySourceService.CreateDisplayEffect(
                new ImageViewerDisplaySettings(PseudoColorPalette.None, DisplayAdjustment.Identity with { Brightness = 0.2 }));
            Assert.Equal((double)DisplayLutMode.PerChannelIndexed, Assert.IsType<DisplayLutShaderEffect>(adjustable).LutMode);
        }

        private static byte[] ReadOutputPixel(System.Windows.Media.Imaging.BitmapSource source, ImageViewerDisplaySettings? settings = null)
        {
            System.Windows.Media.Imaging.BitmapSource result = ImageViewerDisplaySourceService.ApplyDisplayLut(source, settings ?? Hot);
            byte[] pixels = new byte[result.PixelWidth * result.PixelHeight * 4];
            result.CopyPixels(pixels, result.PixelWidth * 4, 0);
            return pixels;
        }

        private static System.Windows.Media.Imaging.BitmapSource CreateBitmap(int width, int height, byte[] pixels)
        {
            return CreateBitmap(width, height, System.Windows.Media.PixelFormats.Bgra32, pixels);
        }

        private static System.Windows.Media.Imaging.BitmapSource CreateBitmap(int width, int height, System.Windows.Media.PixelFormat format, byte[] pixels)
        {
            System.Windows.Media.Imaging.BitmapSource bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width,
                height,
                96,
                96,
                format,
                null,
                pixels,
                width * ((format.BitsPerPixel + 7) / 8));
            bitmap.Freeze();
            return bitmap;
        }

        private static System.Windows.Media.Imaging.BitmapSource CreateGray16(ushort value)
        {
            return CreateBitmap(1, 1, System.Windows.Media.PixelFormats.Gray16, [(byte)value, (byte)(value >> 8)]);
        }

        /// <summary>
        /// 读取着色器源码：WPF 的 &lt;Resource&gt; 项在 &lt;程序集&gt;.g.resources 容器里，键是小写化的相对路径。
        /// </summary>
        private static string ReadEmbeddedShaderSource()
        {
            System.Reflection.Assembly assembly = typeof(ImageViewerDisplaySourceService).Assembly;
            string? container = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(container);

            using Stream containerStream = assembly.GetManifestResourceStream(container)!;
            using var resourceReader = new System.Resources.ResourceReader(containerStream);
            foreach (System.Collections.DictionaryEntry entry in resourceReader)
            {
                if (entry.Key is string key &&
                    key.EndsWith("shaders/displayluteffect.ps.hlsl", StringComparison.OrdinalIgnoreCase) &&
                    entry.Value is Stream shaderStream)
                {
                    using (shaderStream)
                    using (var reader = new StreamReader(shaderStream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }

            throw new InvalidOperationException("Display LUT shader source was not found in the g.resources container.");
        }
    }
}
