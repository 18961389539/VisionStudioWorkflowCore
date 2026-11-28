using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

internal sealed record ReplayImageManifest(int Version, ReplayImageEntry[] Files)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int MaxFiles = 4096;
    private const int MaxCharacters = 1_000_000;

    public static ReplayImageManifest Parse(string value)
    {
        if (value.Length > MaxCharacters)
            throw new InvalidOperationException("image.load manifestJson exceeds 1,000,000 characters.");
        ReplayImageManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ReplayImageManifest>(value, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("image.load manifestJson is not a valid replay manifest.", ex); }
        if (manifest is not { Version: 1, Files.Length: > 0 and <= MaxFiles })
            throw new InvalidOperationException($"image.load manifest requires version 1 and 1..{MaxFiles} files.");

        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) ||
                entry.Path.Split(['/', '\\']).Any(segment => !IsRelativeSegment(segment)) ||
                !ImageLoadNode.IsImageFile(entry.Path) || entry.Sha256 is not { Length: 64 } ||
                !entry.Sha256.All(Uri.IsHexDigit) || !paths.Add(entry.Path.Replace('\\', '/')))
                throw new InvalidOperationException("image.load manifest entries require unique relative image paths and SHA-256 hashes.");
        }
        return manifest;
    }

    public static async Task<ReplayImageManifest> CaptureAsync(
        string root, IReadOnlyList<string> files, IReplayInputResolver authorization, int maxMegabytes, CancellationToken ct)
    {
        if (files.Count > MaxFiles)
            throw new InvalidOperationException($"image.load manifest supports at most {MaxFiles} files.");
        var entries = new ReplayImageEntry[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, files[i]).Replace(Path.DirectorySeparatorChar, '/');
            var path = ResolveFile(root, relative, authorization);
            await using var input = ImageLoadNode.OpenBounded(path, maxMegabytes);
            entries[i] = new ReplayImageEntry(relative, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant());
        }
        return new ReplayImageManifest(1, entries);
    }

    public static async Task ValidateAsync(
        string root, ReplayImageManifest manifest, IReplayInputResolver authorization, int maxMegabytes, CancellationToken ct)
    {
        // Resolve the complete set before hashing. Missing/unsafe entries retain their
        // actionable exception and take precedence over an earlier file's content change.
        var paths = new string[manifest.Files.Length];
        for (var i = 0; i < manifest.Files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            paths[i] = ResolveFile(root, manifest.Files[i].Path, authorization);
        }

        for (var i = 0; i < manifest.Files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = manifest.Files[i];
            await using var input = ImageLoadNode.OpenBounded(paths[i], maxMegabytes);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"image.load manifest image content changed: '{entry.Path}' (preflight entry {i}; SHA-256 mismatch: expected {entry.Sha256}, actual {actual}).");
        }
    }

    public static string ResolveFile(string root, string relative, IReplayInputResolver authorization)
    {
        var segments = relative.Split(['/', '\\']);
        var path = root;
        foreach (var segment in segments)
        {
            if (!IsRelativeSegment(segment))
                throw new InvalidOperationException($"image.load invalid relative manifest path '{relative}'.");
            path = Path.Combine(path, segment);
            if (!Path.Exists(path))
                throw new FileNotFoundException($"image.load manifest image is missing: '{relative}'.", path);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"image.load manifest does not follow linked paths: '{relative}'.");
        }
        var resolved = authorization.Resolve(path);
        if (resolved is null || !File.Exists(resolved))
            throw new InvalidOperationException($"image.load manifest image is not an authorized file: '{relative}'.");
        return resolved;
    }

    private static bool IsRelativeSegment(string segment)
        => !string.IsNullOrWhiteSpace(segment) && !segment.EndsWith('.') && !segment.EndsWith(' ')
            && !segment.Contains(':') && !segment.Any(char.IsControl);

    public string ToJson()
    {
        var value = JsonSerializer.Serialize(this, Json);
        if (value.Length > MaxCharacters)
            throw new InvalidOperationException("image.load generated manifest exceeds 1,000,000 characters.");
        return value;
    }
}

internal sealed record ReplayImageEntry(string Path, string Sha256);
