using System.Windows.Media;
using ImageViewer.Models;
using ImageViewer.Rendering;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// <see cref="RoiColor"/> 的解析、格式化与 WPF 桥接测试。
    /// Chinese: 重点守住持久化格式兼容——既有会话文件里是 "#AARRGGBB"。
    /// English: Parsing, formatting and WPF-bridge tests for RoiColor. The format assertions guard
    /// backward compatibility with existing session files that store "#AARRGGBB".
    /// </summary>
    public class RoiColorTests
    {
        [Fact]
        public void ToString_EmitsAarrggbb()
        {
            Assert.Equal("#FF00FFFF", RoiColors.Cyan.ToString());
            Assert.Equal("#FF000000", RoiColors.Black.ToString());
            Assert.Equal("#00000000", RoiColor.FromArgb(0, 0, 0, 0).ToString());
        }

        [Fact]
        public void ToString_MatchesWpfColorFormat()
        {
            foreach (RoiColor color in new[]
                     {
                         RoiColors.Cyan, RoiColors.Gold, RoiColors.LimeGreen,
                         RoiColors.Coral, RoiColors.DeepSkyBlue, RoiColors.Orchid
                     })
            {
                Assert.Equal(color.ToColor().ToString(System.Globalization.CultureInfo.InvariantCulture), color.ToString());
            }
        }

        [Fact]
        public void Parse_Aarrggbb_RoundTrips()
        {
            Assert.Equal(RoiColors.Cyan, RoiColor.Parse("#FF00FFFF"));
            Assert.Equal(RoiColor.FromArgb(128, 1, 2, 3), RoiColor.Parse("#80010203"));
        }

        [Fact]
        public void Parse_Rrggbb_AssumesOpaqueAlpha()
        {
            Assert.Equal(RoiColor.FromArgb(255, 255, 215, 0), RoiColor.Parse("#FFD700"));
        }

        [Fact]
        public void Parse_ShortRgb_ExpandsDigits()
        {
            Assert.Equal(RoiColor.FromArgb(255, 255, 255, 255), RoiColor.Parse("#FFF"));
            Assert.Equal(RoiColor.FromArgb(255, 0, 255, 255), RoiColor.Parse("#0FF"));
        }

        [Fact]
        public void Parse_NamedColor_IsCaseInsensitive()
        {
            Assert.Equal(RoiColors.Yellow, RoiColor.Parse("Yellow"));
            Assert.Equal(RoiColors.Yellow, RoiColor.Parse("yellow"));
        }

        [Fact]
        public void Parse_InvalidInput_ReturnsNullInsteadOfThrowing()
        {
            Assert.Null(RoiColor.Parse(null));
            Assert.Null(RoiColor.Parse(string.Empty));
            Assert.Null(RoiColor.Parse("   "));
            Assert.Null(RoiColor.Parse("#GGGGGG"));
            Assert.Null(RoiColor.Parse("#12345"));
            Assert.Null(RoiColor.Parse("NotAColor"));
        }

        [Fact]
        public void WpfBridge_RoundTripsEveryComponent()
        {
            var wpfColor = Color.FromArgb(12, 34, 56, 78);

            RoiColor roiColor = wpfColor.ToRoiColor();

            Assert.Equal(12, roiColor.A);
            Assert.Equal(34, roiColor.R);
            Assert.Equal(56, roiColor.G);
            Assert.Equal(78, roiColor.B);
            Assert.Equal(wpfColor, roiColor.ToColor());
        }

        [Fact]
        public void NamedColors_MatchWpfColors()
        {
            Assert.Equal(Colors.Black, RoiColors.Black.ToColor());
            Assert.Equal(Colors.White, RoiColors.White.ToColor());
            Assert.Equal(Colors.Cyan, RoiColors.Cyan.ToColor());
            Assert.Equal(Colors.Red, RoiColors.Red.ToColor());
            Assert.Equal(Colors.Green, RoiColors.Green.ToColor());
            Assert.Equal(Colors.Yellow, RoiColors.Yellow.ToColor());
            Assert.Equal(Colors.Magenta, RoiColors.Magenta.ToColor());
            Assert.Equal(Colors.Gold, RoiColors.Gold.ToColor());
            Assert.Equal(Colors.Lime, RoiColors.Lime.ToColor());
            Assert.Equal(Colors.LimeGreen, RoiColors.LimeGreen.ToColor());
            Assert.Equal(Colors.Coral, RoiColors.Coral.ToColor());
            Assert.Equal(Colors.Orchid, RoiColors.Orchid.ToColor());
            Assert.Equal(Colors.Pink, RoiColors.Pink.ToColor());
            Assert.Equal(Colors.Turquoise, RoiColors.Turquoise.ToColor());
            Assert.Equal(Colors.DeepSkyBlue, RoiColors.DeepSkyBlue.ToColor());
            Assert.Equal(Colors.LightGreen, RoiColors.LightGreen.ToColor());
            Assert.Equal(Colors.Orange, RoiColors.Orange.ToColor());
        }
    }
}
