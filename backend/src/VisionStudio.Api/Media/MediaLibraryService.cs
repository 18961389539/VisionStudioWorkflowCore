using System.Security.Cryptography;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Media;

public sealed record MediaLibraryStatus(
    string Root,
    int CollectionCount,
    int ItemCount,
    int NgItemCount,
    long TotalBytes,
    long MaxImportBytes,
    long MaxLibraryBytes,
    IReadOnlyList<string> AllowedExtensions);

public sealed record MediaCollectionDescriptor(
    string Name,
    string Source,
    int ItemCount,
    int NgItemCount,
    long TotalBytes,
    DateTimeOffset? LastModifiedAt);

public sealed record MediaItemDescriptor(
    string RelativePath,
    string Source,
    string Collection,
    string Label,
    string Name,
    string Extension,
    long Bytes,
    DateTimeOffset ModifiedAt);

public sealed record ResolvedMediaSource(string Source, string RelativePath, string PhysicalPath, bool IsDirectory);

/// <summary>
/// Owns the only user-facing filesystem namespace for offline vision images.
/// Browser/API callers receive media:// logical references and never server absolute paths.
/// </summary>
public sealed class MediaLibraryService
{
    private static readonly string[] SupportedExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff"];
    private static readonly HashSet<string> Supported = new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Labels = new(new[] { "Unlabeled", "OK", "NG", "Review" }, StringComparer.OrdinalIgnoreCase);

    private readonly string _root;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly long _criticalFreeBytes;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public MediaLibraryService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("MediaLibrary:RootPath");
        // R05：相对配置路径也必须相对**数据根**解析（而非打包目录）；否则发布后媒体库会落在包内，
        // 与备份的 system/media 段（数据根/media）不一致 —— 备份清单与还原目标就此错位。
        _root = VisionStudioDataRoot.ResolveMediaRoot(environment.ContentRootPath, configured);
        Directory.CreateDirectory(_root);
        EnsureNoReparsePoint(_root);
        MaxImportBytes = Math.Max(1_048_576, configuration.GetValue<long?>("MediaLibrary:MaxImportBytes") ?? 104_857_600);
        MaxLibraryBytes = Math.Max(MaxImportBytes, configuration.GetValue<long?>("MediaLibrary:MaxLibraryBytes") ?? 20L * 1024 * 1024 * 1024);
        MaxBatchBytes = Math.Max(MaxImportBytes, configuration.GetValue<long?>("MediaLibrary:MaxBatchBytes") ?? 512L * 1024 * 1024);
        MaxBatchFiles = Math.Clamp(configuration.GetValue<int?>("MediaLibrary:MaxBatchFiles") ?? 200, 1, 1000);
        _criticalFreeBytes = Math.Max(0, configuration.GetValue<long?>("Storage:CriticalFreeBytes") ?? 512L * 1024 * 1024);
    }

    public long MaxImportBytes { get; }
    public long MaxLibraryBytes { get; }
    public long MaxBatchBytes { get; }
    public int MaxBatchFiles { get; }
    public IReadOnlyList<string> AllowedExtensions => SupportedExtensions;

    public void EnsureIncomingRequestCapacity(long contentLength)
    {
        if (contentLength <= 0) return;
        if (contentLength > MaxBatchBytes + 1024 * 1024)
            throw new ApiValidationException($"Media request exceeds configured MaxBatchBytes ({MaxBatchBytes}) plus multipart overhead.");
        EnsureImportCapacity(contentLength);
    }

    public MediaLibraryStatus Status()
    {
        var items = EnumerateItems(null, null).ToArray();
        return new MediaLibraryStatus(
            "media://",
            ListCollections().Count,
            items.Length,
            items.Count(x => x.Label.Equals("NG", StringComparison.OrdinalIgnoreCase)),
            items.Sum(x => x.Bytes),
            MaxImportBytes,
            MaxLibraryBytes,
            SupportedExtensions);
    }

    public IReadOnlyList<MediaCollectionDescriptor> ListCollections()
    {
        Directory.CreateDirectory(_root);
        EnsureNoReparsePoint(_root);
        return Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !IsReparsePoint(path))
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var items = EnumerateItems(name, null).ToArray();
                return new MediaCollectionDescriptor(
                    name,
                    ToMediaUri(name),
                    items.Length,
                    items.Count(x => x.Label.Equals("NG", StringComparison.OrdinalIgnoreCase)),
                    items.Sum(x => x.Bytes),
                    items.Length == 0 ? null : items.Max(x => x.ModifiedAt));
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<MediaItemDescriptor> ListItems(string? collection, string? label, int take = 200)
        => EnumerateItems(collection, label)
            .OrderByDescending(x => x.ModifiedAt)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(take, 1, 1000))
            .ToArray();

    public MediaCollectionDescriptor CreateCollection(string name)
    {
        _mutationGate.Wait();
        try
        {
            var safe = NormalizeSegment(name, "collection");
            var path = ResolveRelativePath(safe, allowMissingLeaf: true);
            Directory.CreateDirectory(path);
            EnsureNoReparseTraversal(path);
            return ListCollections().Single(x => x.Name.Equals(safe, StringComparison.OrdinalIgnoreCase));
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<MediaItemDescriptor> ImportAsync(
        string collection,
        string? label,
        string originalName,
        Stream input,
        long length,
        CancellationToken ct)
    {
        if (length <= 0) throw new ApiValidationException("Uploaded image is empty.");
        if (length > MaxImportBytes) throw new ApiValidationException($"Image exceeds MediaLibrary MaxImportBytes ({MaxImportBytes} bytes).");

        await _mutationGate.WaitAsync(ct);
        try
        {
        EnsureImportCapacity(length);
        var safeCollection = NormalizeSegment(collection, "collection");
        var safeLabel = NormalizeLabel(label);
        var fileName = Path.GetFileName(originalName);
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, originalName, StringComparison.Ordinal))
            throw new ApiValidationException("Upload filename must not contain a path.");
        if (!Supported.Contains(Path.GetExtension(fileName)))
            throw new ApiValidationException($"Unsupported media extension '{Path.GetExtension(fileName)}'. Allowed: {string.Join(", ", SupportedExtensions)}.");
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ApiValidationException("Upload filename contains invalid characters.");

        var folderRelative = $"{safeCollection}/{safeLabel}";
        var folder = ResolveRelativePath(folderRelative, allowMissingLeaf: true);
        Directory.CreateDirectory(folder);
        EnsureNoReparseTraversal(folder);

        var destination = UniqueDestination(folder, fileName);
        var temp = destination + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 64 * 1024, ct);
                await output.FlushAsync(ct);
                if (output.Length > MaxImportBytes)
                    throw new ApiValidationException($"Uploaded image exceeds MediaLibrary MaxImportBytes ({MaxImportBytes} bytes).");
            }
            ValidateImageSignature(temp, Path.GetExtension(fileName));
            File.Move(temp, destination);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        return Describe(destination);
        }
        finally { _mutationGate.Release(); }
    }

    public void DeleteItem(string relativePath)
    {
        _mutationGate.Wait();
        try
        {
            var full = ResolveRelativePath(relativePath, allowMissingLeaf: false);
            if (!File.Exists(full)) throw new ApiNotFoundException($"Media item '{relativePath}' does not exist.");
            if (!Supported.Contains(Path.GetExtension(full))) throw new ApiValidationException("Only supported image files can be removed through the media API.");
            File.Delete(full);
            RemoveEmptyParents(Path.GetDirectoryName(full));
        }
        finally { _mutationGate.Release(); }
    }

    public ResolvedMediaSource ResolveSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ApiValidationException("Media source is required.");
        var relative = NormalizeSource(source);
        var full = ResolveRelativePath(relative, allowMissingLeaf: false);
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full)) throw new ApiNotFoundException($"Media source '{ToMediaUri(relative)}' does not exist.");
        if (!isDirectory && !Supported.Contains(Path.GetExtension(full))) throw new ApiValidationException("Media source is not a supported image.");
        if (isDirectory && !EnumerateSupportedFiles(full).Any()) throw new ApiValidationException($"Media source '{ToMediaUri(relative)}' contains no supported images.");
        return new ResolvedMediaSource(ToMediaUri(relative), relative, full, isDirectory);
    }

    public string ResolveItemPath(string relativePath)
    {
        var full = ResolveRelativePath(relativePath, allowMissingLeaf: false);
        if (!File.Exists(full) || !Supported.Contains(Path.GetExtension(full)))
            throw new ApiNotFoundException($"Media item '{relativePath}' does not exist.");
        return full;
    }

    public void SeedCollectionFromDirectory(string collection, string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        var safeCollection = NormalizeSegment(collection, "collection");
        var target = ResolveRelativePath($"{safeCollection}/Unlabeled", allowMissingLeaf: true);
        Directory.CreateDirectory(target);
        if (EnumerateSupportedFiles(target).Any()) return;
        foreach (var source in EnumerateSupportedFiles(sourceDirectory))
        {
            var destination = Path.Combine(target, Path.GetFileName(source));
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
        }
    }

    public string ToMediaUri(string relativePath) => "media://" + NormalizeRelative(relativePath);



    private static void ValidateImageSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(header);
        var ext = extension.ToLowerInvariant();
        var valid = ext switch
        {
            ".png" => read >= 8 && header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => read >= 2 && header[0] == 0xFF && header[1] == 0xD8,
            ".bmp" => read >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M',
            ".tif" or ".tiff" => read >= 4 &&
                ((header[0] == (byte)'I' && header[1] == (byte)'I' && (header[2] == 42 || header[2] == 43) && header[3] == 0) ||
                 (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0 && (header[3] == 42 || header[3] == 43))),
            _ => false
        };
        if (!valid) throw new ApiValidationException($"Uploaded file content does not match the declared image extension '{extension}'.");
    }

    private void EnsureImportCapacity(long incomingBytes)
    {
        var currentBytes = DirectoryBytes(_root);
        if (MaxLibraryBytes > 0 && currentBytes + incomingBytes > MaxLibraryBytes)
            throw new ApiConflictException($"Media Library quota would be exceeded. Current {currentBytes} bytes, incoming {incomingBytes} bytes, quota {MaxLibraryBytes} bytes.");
        try
        {
            var root = Path.GetPathRoot(_root);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace - incomingBytes <= _criticalFreeBytes)
                    throw new ApiUnavailableException("Not enough free disk space to import media while preserving the critical free-space reserve.");
            }
        }
        catch (IOException ex)
        {
            throw new ApiUnavailableException($"Unable to verify media storage capacity: {ex.Message}", ex);
        }
    }

    private static long DirectoryBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private IEnumerable<MediaItemDescriptor> EnumerateItems(string? collection, string? label)
    {
        var root = _root;
        if (!string.IsNullOrWhiteSpace(collection))
            root = ResolveRelativePath(NormalizeSegment(collection, "collection"), allowMissingLeaf: true);
        if (!Directory.Exists(root)) yield break;
        EnsureNoReparseTraversal(root);

        var normalizedLabel = string.IsNullOrWhiteSpace(label) ? null : NormalizeLabel(label);
        foreach (var path in EnumerateSupportedFiles(root))
        {
            var item = Describe(path);
            if (normalizedLabel is not null && !item.Label.Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase)) continue;
            yield return item;
        }
    }

    private MediaItemDescriptor Describe(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var collection = segments.Length > 0 ? segments[0] : "Unsorted";
        var label = segments.Length > 1 && Labels.Contains(segments[1]) ? NormalizeLabel(segments[1]) : "Unlabeled";
        var info = new FileInfo(fullPath);
        return new MediaItemDescriptor(relative, ToMediaUri(relative), collection, label, info.Name, info.Extension.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
    }

    private IEnumerable<string> EnumerateSupportedFiles(string root)
    {
        if (File.Exists(root))
        {
            if (Supported.Contains(Path.GetExtension(root))) yield return root;
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!Supported.Contains(Path.GetExtension(path))) continue;
            EnsureNoReparseTraversal(path);
            yield return path;
        }
    }

    private string ResolveRelativePath(string relativePath, bool allowMissingLeaf)
    {
        var relative = NormalizeRelative(relativePath);
        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, _pathComparison) && !string.Equals(full, _root, _pathComparison))
            throw new ApiValidationException("Media path escapes the configured Media Root.");
        var check = allowMissingLeaf && !File.Exists(full) && !Directory.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (!string.IsNullOrWhiteSpace(check) && (Directory.Exists(check) || File.Exists(check))) EnsureNoReparseTraversal(check!);
        return full;
    }

    private static string NormalizeSource(string source)
    {
        var text = source.Trim().Replace('\\', '/');
        if (text.StartsWith("media://", StringComparison.OrdinalIgnoreCase)) text = text[8..];
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(text) || text.Contains(':'))
            throw new ApiValidationException("File Camera source must be a media:// reference inside the configured Media Root; absolute server paths are forbidden.");
        return NormalizeRelative(text);
    }

    private static string NormalizeRelative(string value)
    {
        var text = value.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(text)) throw new ApiValidationException("Media relative path is required.");
        if (Path.IsPathRooted(text) || text.Contains(':')) throw new ApiValidationException("Absolute media paths are forbidden.");
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(x => x is "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ApiValidationException("Media path contains an invalid or traversal segment.");
        return string.Join('/', segments);
    }

    private static string NormalizeSegment(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ApiValidationException($"Media {kind} is required.");
        var text = value.Trim();
        if (text is "." or ".." || text.Contains('/') || text.Contains('\\') || text.Contains(':') || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ApiValidationException($"Media {kind} must be one safe path segment.");
        return text;
    }

    private static string NormalizeLabel(string? label)
    {
        var value = string.IsNullOrWhiteSpace(label) ? "Unlabeled" : label.Trim();
        var match = Labels.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ApiValidationException($"Unknown media label '{value}'. Allowed: {string.Join(", ", Labels)}.");
    }

    private string UniqueDestination(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique media filename.");
    }

    private void RemoveEmptyParents(string? directory)
    {
        while (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, _root, _pathComparison))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private void EnsureNoReparseTraversal(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, full);
        var current = _root;
        EnsureNoReparsePoint(current);
        if (relative == ".") return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current)) EnsureNoReparsePoint(current);
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if (IsReparsePoint(path)) throw new ApiValidationException("Symbolic links/reparse points are not allowed inside Media Root.");
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
