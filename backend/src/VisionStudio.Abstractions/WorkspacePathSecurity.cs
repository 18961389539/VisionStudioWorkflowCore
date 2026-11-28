namespace VisionStudio.Abstractions;

/// <summary>
/// Confines workflow-supplied paths to an explicit workspace root. Both lexical traversal and
/// existing symlink/junction targets are checked before a node performs file I/O.
/// </summary>
public static class WorkspacePathSecurity
{
    public static string ResolveRoot(string? configuredRoot, string workingDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configuredRoot) ? "." : configuredRoot.Trim();
        RejectRootedOrTraversal(value, "allowedRoot");
        var root = Path.GetFullPath(Path.Combine(workingDirectory, value));
        EnsureWithin(workingDirectory, root, "allowedRoot");
        return ResolveExistingLink(root, workingDirectory);
    }

    public static string ResolveRelative(string root, string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException($"{label} must be a relative path inside the configured root.");
        var value = relativePath.Trim();
        RejectRootedOrTraversal(value, label);
        var full = Path.GetFullPath(Path.Combine(root, value));
        EnsureWithin(root, full, label);
        return ResolveExistingLink(full, root);
    }

    public static void EnsureWithin(string root, string target, string label)
    {
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal) ||
            relative.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} escapes the configured root; access denied.");
        }
    }

    private static void RejectRootedOrTraversal(string value, string label)
    {
        if (Path.IsPathRooted(value) || HasDrivePrefix(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} must be relative; absolute paths and drive paths are rejected.");
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException($"{label} contains invalid path characters.");
        if (value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException($"{label} must not contain '..' traversal segments.");
    }

    private static bool HasDrivePrefix(string value)
        => value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';

    private static string ResolveExistingLink(string path, string boundary)
    {
        var resolvedBoundary = ResolveFinalTarget(boundary);
        var relative = Path.GetRelativePath(resolvedBoundary, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal) ||
            relative.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path escapes the configured root through a link; access denied.");
        }

        var current = resolvedBoundary;
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            var info = Directory.Exists(current)
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidOperationException("Unable to resolve a filesystem link inside the configured root.");
            EnsureWithin(resolvedBoundary, target.FullName, "linked path");
            current = target.FullName;
        }
        return current;
    }

    private static string ResolveFinalTarget(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return Path.GetFullPath(path);
        var info = Directory.Exists(path)
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is null
            ? Path.GetFullPath(path)
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
    }
}
