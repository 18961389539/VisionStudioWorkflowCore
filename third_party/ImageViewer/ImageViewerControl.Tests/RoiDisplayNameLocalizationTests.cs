using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// ROI 显示名本地化的接线守卫。
    /// Chinese: RoiBase 下沉到 Core 后不再直接引用本地化资源，改为由本程序集通过模块初始化器安装解析器。
    /// 这条接线一旦断开，所有 ROI 显示名会静默退化成 CLR 类型名（例如 "CircleRoi"），所以它必须被断言，
    /// 不能只靠"看起来没坏"。
    /// English: Guard for the display-name wiring. After RoiBase moved to Core it no longer references the
    /// localization resources directly; this assembly installs the resolver from a module initializer. If that
    /// wiring breaks, every ROI display name silently degrades to the CLR type name, so it is asserted here.
    /// </summary>
    public class RoiDisplayNameLocalizationTests
    {
        [Fact]
        public void ControlAssembly_InstallsItsLocalizerAsTheCoreResolver()
        {
            var circle = new CircleRoi();

            Assert.NotNull(RoiDisplayNameResolver.Resolver);
            Assert.Equal(
                RoiDisplayNameLocalizer.GetDisplayName(circle),
                RoiDisplayNameResolver.GetDisplayName(circle));
        }

        [Fact]
        public void RoiDisplayNames_AreLocalizedNotClrTypeNames()
        {
            var circle = new CircleRoi();
            var line = new LineMeasureRoi();

            Assert.False(string.IsNullOrWhiteSpace(circle.DisplayTypeName));
            Assert.False(string.IsNullOrWhiteSpace(line.DisplayTypeName));

            Assert.NotEqual(circle.RoiTypeName, circle.DisplayTypeName);
            Assert.NotEqual(line.RoiTypeName, line.DisplayTypeName);
            Assert.NotEqual(circle.DisplayTypeName, line.DisplayTypeName);
        }
    }
}
