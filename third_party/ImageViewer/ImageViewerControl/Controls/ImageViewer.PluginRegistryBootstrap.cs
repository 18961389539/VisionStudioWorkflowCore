using System;
using ImageViewer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ImageViewer.Controls
{
    internal static class ImageViewerPluginRegistryBootstrap
    {
        public static RoiPluginRegistry CreateDefault()
        {
            return RoiPluginRegistry.CreateBuiltIn();
        }

        public static RoiPluginRegistry Resolve(RoiPluginRegistry? pluginRegistry)
        {
            return pluginRegistry ?? CreateDefault();
        }

        public static RoiPluginRegistry Resolve(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            return serviceProvider.GetService<RoiPluginRegistry>() ?? CreateDefault();
        }
    }
}