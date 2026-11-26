using System.Collections.Generic;
using ImageViewer.Services;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerRecentProjectService
    {
        IReadOnlyList<RecentImageViewerProject> Load(string filePath, int maxCount = 10);

        void Save(string filePath, IEnumerable<RecentImageViewerProject> items);

        IReadOnlyList<RecentImageViewerProject> Touch(IEnumerable<RecentImageViewerProject> items, string filePath, string projectKind, int maxCount = 10);
    }
}
