using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerRecentProjectCatalogTests
    {
        [Fact]
        public void Remember_AndRemoveMissing_PersistProjectListThroughService()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string recentProjectsFilePath = Path.Combine(rootPath, "recent-projects.json");
            var service = new RecordingRecentProjectService();
            var catalog = new ImageViewerRecentProjectCatalog(service, recentProjectsFilePath);

            catalog.Remember(Path.Combine(rootPath, "sample.ivsession"), "session");

            RecentImageViewerProject remembered = Assert.Single(service.LastSavedItems!);
            Assert.Equal("session", remembered.ProjectKind);
            Assert.Equal(Path.Combine(rootPath, "sample.ivsession"), remembered.Path);

            bool removed = catalog.RemoveMissing(remembered.Path);

            Assert.True(removed);
            Assert.Empty(service.LastSavedItems!);
        }

        private sealed class RecordingRecentProjectService : IImageViewerRecentProjectService
        {
            private IReadOnlyList<RecentImageViewerProject> _items = [];

            public IReadOnlyList<RecentImageViewerProject>? LastSavedItems { get; private set; }

            public IReadOnlyList<RecentImageViewerProject> Load(string filePath, int maxCount = 10)
            {
                return _items;
            }

            public void Save(string filePath, IEnumerable<RecentImageViewerProject> items)
            {
                _items = items.ToArray();
                LastSavedItems = _items;
            }

            public IReadOnlyList<RecentImageViewerProject> Touch(IEnumerable<RecentImageViewerProject> items, string filePath, string projectKind, int maxCount = 10)
            {
                return new ImageViewerRecentProjectService().Touch(items, filePath, projectKind, maxCount);
            }
        }
    }
}