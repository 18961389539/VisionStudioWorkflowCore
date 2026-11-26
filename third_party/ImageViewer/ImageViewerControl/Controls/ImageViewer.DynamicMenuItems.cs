using System;
using System.Windows;
using ImageViewer.Localization;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal enum ImageViewerDynamicMenuGroup
    {
        None,
        Drawing,
        Measurement
    }

    internal sealed record ImageViewerDynamicMenuItem(
        string Header,
        string? ToolTip,
        bool IsEnabled,
        ImageViewerMenuCommandTag Tag,
        ImageViewerDynamicMenuGroup Group = ImageViewerDynamicMenuGroup.None,
        Func<FrameworkElement>? CreateIcon = null)
    {
        public static ImageViewerDynamicMenuItem FromRoiTool(RoiToolDescriptor tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return new ImageViewerDynamicMenuItem(
                tool.Header,
                ToolTip: null,
                IsEnabled: true,
                Tag: new ImageViewerRoiToolMenuTag(tool.Header, tool.Activate),
                Group: tool.IsMeasurement ? ImageViewerDynamicMenuGroup.Measurement : ImageViewerDynamicMenuGroup.Drawing,
                CreateIcon: tool.CreateIcon);
        }

        public static ImageViewerDynamicMenuItem FromRecentProject(RecentImageViewerProject project)
        {
            ArgumentNullException.ThrowIfNull(project);
            return new ImageViewerDynamicMenuItem(
                Header: UiText.Format("RecentProjectHeader", project.DisplayName, project.ProjectKind),
                ToolTip: string.IsNullOrWhiteSpace(project.Path) ? null : project.Path,
                IsEnabled: true,
                Tag: new ImageViewerRecentProjectMenuTag(project.Path));
        }

        public static ImageViewerDynamicMenuItem EmptyRecentProject { get; } = new(
            Header: UiText.Get("RecentProjectsEmpty"),
            ToolTip: null,
            IsEnabled: false,
            Tag: ImageViewerRecentProjectMenuTag.Empty);
    }

    internal sealed class ImageViewerRoiToolMenuTag : ImageViewerMenuCommandTag
    {
        private readonly Action<ImageViewer> _activate;

        public ImageViewerRoiToolMenuTag(string toolName, Action<ImageViewer> activate)
        {
            ToolName = string.IsNullOrWhiteSpace(toolName) ? UiText.Get("ActiveToolGeneric") : toolName;
            _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        }

        public string ToolName { get; }

        public void Activate(ImageViewer owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            _activate(owner);
        }
    }

    internal sealed class ImageViewerRecentProjectMenuTag : ImageViewerMenuCommandTag
    {
        public ImageViewerRecentProjectMenuTag(string projectPath)
        {
            ProjectPath = projectPath ?? string.Empty;
        }

        public string ProjectPath { get; }

        public static ImageViewerRecentProjectMenuTag Empty { get; } = new(string.Empty);
    }
}
