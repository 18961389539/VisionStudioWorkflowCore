using System.Runtime.CompilerServices;
using ImageViewer.Models;

// CA2255 建议库代码不要用 ModuleInitializer。这里是刻意的例外：本地化显示名过去是直接编译进 RoiBase 的，
// 因此"只要控件程序集被加载就一定有本地化"。改为 Core 侧解析入口后，模块初始化器是唯一不依赖调用点顺序的等价机制
// （改成在某个组合根里赋值，任何早于该组合根的显示名访问都会退化成类型名）。
// CA2255 recommends against ModuleInitializer in libraries. This is a deliberate exception: localization used to be
// compiled straight into RoiBase, so "loaded assembly implies localized names" was guaranteed. With the resolver moved
// to Core, a module initializer is the only mechanism that keeps that guarantee independent of call-site ordering.
#pragma warning disable CA2255

namespace ImageViewer.Localization
{
    /// <summary>
    /// 把控件侧的本地化显示名安装到 Core 的解析入口。
    /// Chinese: 用模块初始化器保证"只要控件程序集被加载，本地化显示名就已接上"，不依赖任何调用点的先后顺序。
    /// English: Installs the control-side localized display names into the Core resolver from a module initializer.
    /// </summary>
    internal static class RoiDisplayNameResolverInstaller
    {
        [ModuleInitializer]
        internal static void Install()
        {
            RoiDisplayNameResolver.Resolver = RoiDisplayNameLocalizer.GetDisplayName;
        }
    }
}
#pragma warning restore CA2255
