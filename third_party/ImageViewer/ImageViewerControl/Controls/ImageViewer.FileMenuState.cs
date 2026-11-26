using System.Collections.Generic;
using System.Linq;

namespace ImageViewer.Controls
{
    internal readonly record struct ImageViewerFileMenuStateInput(
        bool HasRois,
        bool HasContent,
        bool AutoSaveEnabled,
        IReadOnlyList<ImageViewerDynamicMenuItem> RecentProjects);

    internal sealed record ImageViewerFileMenuState(
        bool SaveRoisEnabled,
        bool SaveSessionEnabled,
        bool ExportProjectPackageEnabled,
        bool AutoSaveChecked,
        bool RecentProjectsEnabled,
        IReadOnlyList<ImageViewerDynamicMenuItem> RecentProjects)
    {
        public static ImageViewerFileMenuState Empty { get; } = new(
            SaveRoisEnabled: false,
            SaveSessionEnabled: false,
            ExportProjectPackageEnabled: false,
            AutoSaveChecked: false,
            RecentProjectsEnabled: false,
            RecentProjects: Array.Empty<ImageViewerDynamicMenuItem>());
    }

    internal static class ImageViewerFileMenuStateEvaluator
    {
        public static ImageViewerFileMenuState Evaluate(ImageViewerFileMenuStateInput input)
        {
            IReadOnlyList<ImageViewerDynamicMenuItem> recentProjects = input.RecentProjects.Count > 0
                ? input.RecentProjects
                : new[] { ImageViewerDynamicMenuItem.EmptyRecentProject };
            bool hasRecentProjects = recentProjects.Any(item => item.IsEnabled);

            return new ImageViewerFileMenuState(
                SaveRoisEnabled: input.HasRois,
                SaveSessionEnabled: input.HasContent,
                ExportProjectPackageEnabled: input.HasContent,
                AutoSaveChecked: input.AutoSaveEnabled,
                RecentProjectsEnabled: hasRecentProjects,
                RecentProjects: recentProjects);
        }
    }
}