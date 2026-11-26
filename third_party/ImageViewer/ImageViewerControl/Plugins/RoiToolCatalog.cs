using System;
using System.Collections.Generic;
using System.Linq;

namespace ImageViewer.Plugins
{
    internal static class RoiToolCatalog
    {
        public static bool ContainsFilteredTools(IRoiPluginDefinition plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            return plugin.DrawingTools.Any(tool => !ShouldExpose(tool));
        }

        public static IReadOnlyList<RoiToolDescriptor> GetVisibleTools(IRoiPluginDefinition plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            return plugin.DrawingTools
                .Where(ShouldExpose)
                .ToArray();
        }

        public static IEnumerable<RoiToolDescriptor> OrderVisibleTools(IEnumerable<IRoiPluginDefinition> plugins)
        {
            ArgumentNullException.ThrowIfNull(plugins);
            return plugins
                .SelectMany(plugin => plugin.DrawingTools)
                .Where(ShouldExpose)
                .OrderBy(static tool => tool.MenuOrder)
                .ThenBy(static tool => tool.Header, StringComparer.CurrentCultureIgnoreCase);
        }

        private static bool ShouldExpose(RoiToolDescriptor tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return tool.IsVisible;
        }
    }
}
