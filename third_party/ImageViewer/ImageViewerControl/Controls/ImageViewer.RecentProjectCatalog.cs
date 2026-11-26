using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageViewer.Abstractions;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerRecentProjectCatalog
    {
        private readonly IImageViewerRecentProjectService _recentProjectService;
        private readonly string _recentProjectsFilePath;
        private RecentImageViewerProject[] _recentProjects;

        public ImageViewerRecentProjectCatalog(IImageViewerRecentProjectService recentProjectService, string recentProjectsFilePath)
        {
            _recentProjectService = recentProjectService ?? throw new ArgumentNullException(nameof(recentProjectService));
            ArgumentException.ThrowIfNullOrWhiteSpace(recentProjectsFilePath);

            _recentProjectsFilePath = recentProjectsFilePath;
            _recentProjects = LoadRecentProjects();
        }

        public IReadOnlyList<ImageViewerDynamicMenuItem> GetMenuItems()
        {
            Reload();
            return _recentProjects
                .Where(item => File.Exists(item.Path))
                .Select(ImageViewerDynamicMenuItem.FromRecentProject)
                .ToArray();
        }

        public void Remember(string filePath, string projectKind)
        {
            Reload();
            _recentProjects = AsRecentProjectArray(_recentProjectService.Touch(_recentProjects, filePath, projectKind));
            _recentProjectService.Save(_recentProjectsFilePath, _recentProjects);
        }

        public bool RemoveMissing(string filePath)
        {
            Reload();

            RecentImageViewerProject[] updated = _recentProjects
                .Where(item => !PathsEqual(item.Path, filePath))
                .ToArray();

            if (updated.Length == _recentProjects.Length)
            {
                return false;
            }

            _recentProjects = updated;
            _recentProjectService.Save(_recentProjectsFilePath, _recentProjects);
            return true;
        }

        private void Reload()
        {
            _recentProjects = LoadRecentProjects();
        }

        private RecentImageViewerProject[] LoadRecentProjects()
        {
            return AsRecentProjectArray(_recentProjectService.Load(_recentProjectsFilePath));
        }

        private static RecentImageViewerProject[] AsRecentProjectArray(IReadOnlyList<RecentImageViewerProject> recentProjects)
        {
            return recentProjects as RecentImageViewerProject[] ?? [.. recentProjects];
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
