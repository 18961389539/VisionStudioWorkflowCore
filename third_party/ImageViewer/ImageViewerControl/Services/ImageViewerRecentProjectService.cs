using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImageViewer.Abstractions;

namespace ImageViewer.Services
{
    public sealed class ImageViewerRecentProjectService : IImageViewerRecentProjectService
    {
        public IReadOnlyList<RecentImageViewerProject> Load(string filePath, int maxCount = 10)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (!File.Exists(filePath))
            {
                return [];
            }

            List<RecentImageViewerProject> items;
            try
            {
                items = JsonSerializer.Deserialize(
                    File.ReadAllText(filePath),
                    ImageViewerJsonSerializationContext.Default.ListRecentImageViewerProject) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }

            return items
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path))
                .OrderByDescending(item => item.LastOpenedUtc)
                .Take(Math.Max(1, maxCount))
                .ToArray();
        }

        public void Save(string filePath, IEnumerable<RecentImageViewerProject> items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(items);

            ImageViewerAtomicFile.WriteAllText(
                filePath,
                JsonSerializer.Serialize(items.ToList(), ImageViewerJsonSerializationContext.Default.ListRecentImageViewerProject));
        }

        public IReadOnlyList<RecentImageViewerProject> Touch(IEnumerable<RecentImageViewerProject> items, string filePath, string projectKind, int maxCount = 10)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(projectKind);

            var updated = items
                .Where(item => !PathsEqual(item.Path, filePath))
                .Prepend(CreateRecentProject(filePath, projectKind))
                .OrderByDescending(item => item.LastOpenedUtc)
                .Take(Math.Max(1, maxCount))
                .ToArray();

            return updated;
        }

        private static RecentImageViewerProject CreateRecentProject(string filePath, string projectKind)
        {
            string normalizedPath = Path.GetFullPath(filePath);
            return new RecentImageViewerProject(
                Path.GetFileNameWithoutExtension(normalizedPath),
                normalizedPath,
                projectKind,
                DateTimeOffset.UtcNow);
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

    public sealed record RecentImageViewerProject(string DisplayName, string Path, string ProjectKind, DateTimeOffset LastOpenedUtc);
}
