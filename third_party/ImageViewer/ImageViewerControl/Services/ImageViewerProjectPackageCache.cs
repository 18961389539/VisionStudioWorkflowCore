using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ImageViewer.Services;

/// <summary>Disk cache for fully extracted packages. An open marker handle prevents eviction.</summary>
internal static class ImageViewerProjectPackageCache
{
    private const string EntryPrefix = "pkg-";
    private const string CompleteMarker = ".complete";
    private const string LastUsedMarker = ".last-used";
    private const string SavedSessionMarker = ".saved-session";
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEF");
    private static readonly ConditionalWeakTable<ImageViewerSessionData, FileStream> SessionLeases = new();

    public static string GetDirectory(string root, byte[] hash) => Path.Combine(root, EntryPrefix + Convert.ToHexString(hash));

    public static async Task<FileStream> LockAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        string lockPath = Path.Combine(root, ".cache-lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string MarkerPath(string directory) => Path.Combine(directory, CompleteMarker);

    public static void Complete(string directory, long bytes) =>
        File.WriteAllText(MarkerPath(directory), bytes.ToString(CultureInfo.InvariantCulture));

    public static FileStream LeaseEntry(string directory)
    {
        File.WriteAllText(Path.Combine(directory, LastUsedMarker), string.Empty);
        return new FileStream(MarkerPath(directory), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    public static void AttachSessionLease(ImageViewerSessionData session, FileStream lease)
    {
        SessionLeases.AddOrUpdate(session, lease);
    }

    public static FileStream? TryLeaseAsset(string? assetPath, CancellationToken cancellationToken = default)
    {
        DirectoryInfo? entry = FindAssetEntry(assetPath);
        if (entry?.Parent is not { } root)
        {
            return null;
        }

        using FileStream guard = LockAsync(root.FullName, cancellationToken).GetAwaiter().GetResult();
        return LeaseEntry(entry.FullName);
    }

    public static void RemoveEntry(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A package cache entry cannot be a link.");
        }

        string marker = MarkerPath(directory);
        if (File.Exists(marker))
        {
            // The caller holds the root lock, so new readers cannot enter after this check.
            using FileStream exclusive = new(marker, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        Directory.Delete(directory, recursive: true);
    }

    public static void Trim(string root, string current, string autoSaveDirectory, long budget)
    {
        try
        {
            // The root lock ensures no other extraction is using a staging directory.
            foreach (DirectoryInfo staged in new DirectoryInfo(root).EnumerateDirectories("staging-*"))
            {
                if (Guid.TryParseExact(staged.Name["staging-".Length..], "N", out _) &&
                    staged.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1) &&
                    (staged.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    TryRemoveEntry(staged.FullName);
                }
            }

            var entries = new List<(DirectoryInfo Directory, long Bytes, DateTime LastUsed)>();
            foreach (DirectoryInfo entry in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (!IsEntryName(entry.Name) || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var marker = new FileInfo(MarkerPath(entry.FullName));
                if (marker.Exists && long.TryParse(File.ReadAllText(marker.FullName), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes))
                {
                    var lastUsed = new FileInfo(Path.Combine(entry.FullName, LastUsedMarker));
                    entries.Add((entry, bytes, lastUsed.Exists ? lastUsed.LastWriteTimeUtc : marker.LastWriteTimeUtc));
                }
            }

            long totalBytes = entries.Sum(entry => entry.Bytes);
            if (totalBytes <= budget)
            {
                return;
            }

            HashSet<string> recoveryEntries = GetRecoveryEntries(autoSaveDirectory);
            foreach (var entry in entries.OrderBy(entry => entry.LastUsed).ThenBy(entry => entry.Directory.Name, StringComparer.Ordinal))
            {
                string path = entry.Directory.FullName;
                if (totalBytes <= budget)
                {
                    break;
                }
                if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase) ||
                    recoveryEntries.Contains(path) ||
                    // Existing standalone sessions may still refer directly to an older pinned entry.
                    File.Exists(Path.Combine(path, SavedSessionMarker)))
                {
                    continue;
                }

                if (TryRemoveEntry(path))
                {
                    totalBytes -= entry.Bytes;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache cleanup is optional. If a recovery directory cannot be read, keep its assets.
        }
    }

    private static bool TryRemoveEntry(string path)
    {
        try
        {
            RemoveEntry(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HashSet<string> GetRecoveryEntries(string autoSaveDirectory)
    {
        var protectedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(autoSaveDirectory))
        {
            return protectedEntries;
        }

        foreach (string file in Directory.EnumerateFiles(autoSaveDirectory, "*.ivsession"))
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        FindAssetEntry(property.Value.GetString(), autoSaveDirectory) is { } entry)
                    {
                        protectedEntries.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or JsonException)
            {
                // An atomically removed or malformed session cannot be recovered.
            }
        }
        return protectedEntries;
    }

    private static DirectoryInfo? FindAssetEntry(string? path, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = basePath == null ? Path.GetFullPath(path) : Path.GetFullPath(path, Path.GetFullPath(basePath));
            for (DirectoryInfo? directory = new FileInfo(fullPath).Directory; directory?.Parent != null; directory = directory.Parent)
            {
                if (IsEntryName(directory.Parent.Name) &&
                    directory.Name.Equals("assets", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(MarkerPath(directory.Parent.FullName)))
                {
                    return directory.Parent;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
        return null;
    }

    private static bool IsEntryName(string name) =>
        name.Length == EntryPrefix.Length + 64 && name.StartsWith(EntryPrefix, StringComparison.Ordinal) &&
        name.AsSpan(EntryPrefix.Length).IndexOfAnyExcept(HexDigits) < 0;
}
