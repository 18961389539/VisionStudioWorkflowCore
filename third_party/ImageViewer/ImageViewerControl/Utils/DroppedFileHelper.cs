using System;
using System.Collections.Generic;
using System.IO;

namespace ImageViewer.Utils
{
    public enum DroppedFileKind
    {
        Image,
        Session,
        ProjectPackage
    }

    public static class DroppedFileHelper
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp",
            ".gif",
            ".jpg",
            ".jpeg",
            ".png",
            ".tif",
            ".tiff"
        };

        private static readonly HashSet<string> SessionExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ivsession"
        };

        private static readonly HashSet<string> ProjectPackageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ivpkg"
        };

        public static bool TryGetOpenablePath(IEnumerable<string> paths, out string path, out DroppedFileKind kind)
        {
            foreach (string? candidate in paths)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                if (IsSupportedImageFile(candidate))
                {
                    path = candidate;
                    kind = DroppedFileKind.Image;
                    return true;
                }

                if (IsSupportedSessionFile(candidate))
                {
                    path = candidate;
                    kind = DroppedFileKind.Session;
                    return true;
                }

                if (IsSupportedProjectPackageFile(candidate))
                {
                    path = candidate;
                    kind = DroppedFileKind.ProjectPackage;
                    return true;
                }
            }

            path = string.Empty;
            kind = DroppedFileKind.Image;
            return false;
        }

        public static bool IsSupportedImageFile(string filePath)
        {
            return ImageExtensions.Contains(Path.GetExtension(filePath));
        }

        public static bool IsSupportedSessionFile(string filePath)
        {
            return SessionExtensions.Contains(Path.GetExtension(filePath));
        }

        public static bool IsSupportedProjectPackageFile(string filePath)
        {
            return ProjectPackageExtensions.Contains(Path.GetExtension(filePath));
        }
    }
}
