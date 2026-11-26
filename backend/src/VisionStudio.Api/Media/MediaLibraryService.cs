using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using OpenCvSharp;
using VisionStudio.Abstractions;
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
    DateTimeOffset ModifiedAt,
    int? Width = null,
    int? Height = null,
    int? Channels = null,
    int? BitDepth = null);

/// <summary>媒体项分页结果；Total 是 skip/take 之前的过滤后条数。</summary>
public sealed record MediaItemPage(IReadOnlyList<MediaItemDescriptor> Items, int Total);

public sealed record ResolvedMediaSource(string Source, string RelativePath, string PhysicalPath, bool IsDirectory);

public sealed record MediaLinkedSourceDescriptor(
    string Name,
    string Path,
    int ItemCount,
    long TotalBytes,
    DateTimeOffset LastModifiedAt);

public sealed record MediaSourceDescriptor(
    string Name,
    string Source,
    bool IsDirectory,
    int ItemCount,
    string Kind);

public sealed record MediaBulkRequest(
    string Action,
    string[] Paths,
    string? TargetCollection = null,
    string? TargetLabel = null,
    string? TargetName = null);

public sealed record MediaBulkFailure(string Path, string Error);
public sealed record MediaBulkResult(int Succeeded, IReadOnlyList<MediaItemDescriptor> Items, IReadOnlyList<MediaBulkFailure> Failures);

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
    private readonly string _thumbnailRoot;
    private readonly string _linkedSourcesPath;
    private readonly IReplayInputResolver? _replayInputResolver;
    private readonly MediaLibraryIndex? _index;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly long _criticalFreeBytes;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public MediaLibraryService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IReplayInputResolver? replayInputResolver = null,
        SqliteMetadataDatabase? database = null)
    {
        var configured = configuration.GetValue<string>("MediaLibrary:RootPath");
        // R05：相对配置路径也必须相对**数据根**解析（而非打包目录）；否则发布后媒体库会落在包内，
        // 与备份的 system/media 段（数据根/media）不一致 —— 备份清单与还原目标就此错位。
        _root = VisionStudioDataRoot.ResolveMediaRoot(environment.ContentRootPath, configured, configuration);
        Directory.CreateDirectory(_root);
        _thumbnailRoot = Path.Combine(Path.GetDirectoryName(_root)!, "media-thumbnails");
        Directory.CreateDirectory(_thumbnailRoot);
        EnsureNoReparsePoint(_thumbnailRoot);
        EnsureNoReparsePoint(_root);
        _linkedSourcesPath = Path.Combine(_root, ".linked-sources.json");
        _replayInputResolver = replayInputResolver;
        _index = database is null ? null : new MediaLibraryIndex(database);
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
        var limits = new MediaLibraryStatus(
            "media://",
            0,
            0,
            0,
            0,
            MaxImportBytes,
            MaxLibraryBytes,
            SupportedExtensions);
        if (_index is null)
        {
            var items = EnumerateItems(null, null).ToArray();
            return limits with
            {
                CollectionCount = ListCollections().Count,
                ItemCount = items.Length,
                NgItemCount = items.Count(x => x.Label.Equals("NG", StringComparison.OrdinalIgnoreCase)),
                TotalBytes = items.Sum(x => x.Bytes)
            };
        }
        EnsureIndex();
        var status = _index.Status(limits);
        return status with
        {
            CollectionCount = Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
                .Count(path => !IsReparsePoint(path))
        };
    }

    public IReadOnlyList<MediaCollectionDescriptor> ListCollections()
    {
        if (_index is not null)
        {
            EnsureIndex();
            var indexed = _index.Collections().ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !IsReparsePoint(path)))
            {
                var name = Path.GetFileName(path);
                if (!indexed.ContainsKey(name))
                    indexed[name] = new MediaCollectionDescriptor(name, ToMediaUri(name), 0, 0, 0, null);
            }
            return indexed.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
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

    public MediaItemPage ListItems(
        string? collection,
        string? label,
        int skip = 0,
        int take = 200,
        string? search = null,
        int? minWidth = null,
        int? maxWidth = null,
        DateTimeOffset? modifiedAfter = null,
        DateTimeOffset? modifiedBefore = null)
    {
        if (_index is not null)
        {
            EnsureIndex();
            return _index.Query(new MediaItemQuery(
                collection,
                string.IsNullOrWhiteSpace(label) ? null : NormalizeLabel(label),
                search,
                minWidth,
                maxWidth,
                modifiedAfter,
                modifiedBefore,
                skip,
                take));
        }
        var filtered = EnumerateItems(collection, label)
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(item => minWidth is null || item.Width >= minWidth)
            .Where(item => maxWidth is null || item.Width <= maxWidth)
            .Where(item => modifiedAfter is null || item.ModifiedAt >= modifiedAfter)
            .Where(item => modifiedBefore is null || item.ModifiedAt <= modifiedBefore)
            .OrderByDescending(x => x.ModifiedAt)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectiveSkip = Math.Max(0, skip);
        var effectiveTake = Math.Clamp(take, 1, 1000);
        return new MediaItemPage(filtered.Skip(effectiveSkip).Take(effectiveTake).ToArray(), filtered.Length);
    }

    public async Task<MediaCollectionDescriptor> CreateCollectionAsync(string name, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct);
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
        string relativeName,
        Stream input,
        long length,
        CancellationToken ct,
        bool idempotentRelativePath = false)
    {
        if (length <= 0) throw new ApiValidationException("Uploaded image is empty.");
        if (length > MaxImportBytes) throw new ApiValidationException($"Image exceeds MediaLibrary MaxImportBytes ({MaxImportBytes} bytes).");

        await _mutationGate.WaitAsync(ct);
        try
        {
        EnsureImportCapacity(length);
        var safeCollection = NormalizeSegment(collection, "collection");
        var safeLabel = NormalizeLabel(label);
        var safeRelativeName = NormalizeRelative(relativeName);
        var fileName = Path.GetFileName(safeRelativeName.Replace('/', Path.DirectorySeparatorChar));
        if (!Supported.Contains(Path.GetExtension(fileName)))
            throw new ApiValidationException($"Unsupported media extension '{Path.GetExtension(fileName)}'. Allowed: {string.Join(", ", SupportedExtensions)}.");

        var relativeDirectory = Path.GetDirectoryName(safeRelativeName.Replace('/', Path.DirectorySeparatorChar));
        var folderRelative = string.IsNullOrWhiteSpace(relativeDirectory)
            ? $"{safeCollection}/{safeLabel}"
            : $"{safeCollection}/{safeLabel}/{relativeDirectory.Replace(Path.DirectorySeparatorChar, '/')}";
        var folder = ResolveRelativePath(folderRelative, allowMissingLeaf: true);
        Directory.CreateDirectory(folder);
        EnsureNoReparseTraversal(folder);

        var destination = idempotentRelativePath ? Path.Combine(folder, fileName) : UniqueDestination(folder, fileName);
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
            if (idempotentRelativePath && File.Exists(destination) && await FilesEqualAsync(temp, destination, ct))
            {
                File.Delete(temp);
                var existing = Describe(destination);
                _index?.Upsert(existing);
                return existing;
            }
            if (File.Exists(destination)) destination = UniqueDestination(folder, fileName);
            File.Move(temp, destination);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 上传临时文件清理失败：保留原异常，不覆盖导入错误 */ }
            throw;
        }

        var imported = Describe(destination);
        _index?.Upsert(imported);
        return imported;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task DeleteItemAsync(string relativePath, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            var full = ResolveRelativePath(relativePath, allowMissingLeaf: false);
            if (!File.Exists(full)) throw new ApiNotFoundException($"Media item '{relativePath}' does not exist.");
            if (!Supported.Contains(Path.GetExtension(full))) throw new ApiValidationException("Only supported image files can be removed through the media API.");
            File.Delete(full);
            _index?.Remove(NormalizeRelative(relativePath));
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

    public async Task<string> ResolveThumbnailPathAsync(string relativePath, CancellationToken ct = default)
    {
        var physical = ResolveItemPath(relativePath);
        var normalized = NormalizeRelative(relativePath);
        var sourceInfo = new FileInfo(physical);
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{normalized}|{sourceInfo.LastWriteTimeUtc.Ticks}|{sourceInfo.Length}"))).ToLowerInvariant();
        var thumbnail = Path.Combine(_thumbnailRoot, key + ".jpg");
        if (File.Exists(thumbnail)) return thumbnail;
        Directory.CreateDirectory(_thumbnailRoot);

        var metadata = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var image = Cv2.ImRead(physical, ImreadModes.Unchanged);
            if (image.Empty()) throw new ApiValidationException($"Media item '{normalized}' could not be decoded.");
            using var preview = new Mat();
            if (image.Depth() == MatType.CV_8U && image.Channels() is 1 or 3)
                image.CopyTo(preview);
            else if (image.Depth() == MatType.CV_8U && image.Channels() == 4)
                Cv2.CvtColor(image, preview, ColorConversionCodes.BGRA2BGR);
            else
                Cv2.Normalize(image, preview, 0, 255, NormTypes.MinMax, MatType.CV_8U);
            if (preview.Channels() == 4) Cv2.CvtColor(preview, preview, ColorConversionCodes.BGRA2BGR);
            var scale = Math.Min(1d, Math.Min(480d / preview.Width, 320d / preview.Height));
            using var resized = scale >= 1 ? preview.Clone() : new Mat();
            if (scale < 1)
                Cv2.Resize(preview, resized, new Size(), scale, scale, InterpolationFlags.Area);
            if (!Cv2.ImEncode(
                    ".jpg",
                    resized,
                    out var jpeg,
                    [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]))
                throw new ApiUnavailableException("Unable to encode media thumbnail.");
            return (
                Bytes: jpeg,
                Width: image.Width,
                Height: image.Height,
                Channels: image.Channels(),
                BitDepth: BitDepthOf(image.Depth()));
        }, ct);

        var temp = thumbnail + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, metadata.Bytes, ct);
            File.Move(temp, thumbnail, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        var item = Describe(physical) with
        {
            Width = metadata.Width,
            Height = metadata.Height,
            Channels = metadata.Channels,
            BitDepth = metadata.BitDepth
        };
        _index?.Upsert(item);
        return thumbnail;
    }

    public MediaLinkedSourceDescriptor[] ListLinkedSources()
    {
        var links = ReadLinkedSources();
        return links
            .Select(link =>
            {
                if (!Directory.Exists(link.Path)) return link with { ItemCount = 0, TotalBytes = 0 };
                var files = EnumerateSupportedExternalFiles(link.Path);
                return link with
                {
                    ItemCount = files.Length,
                    TotalBytes = files.Sum(x => x.Length),
                    LastModifiedAt = files.Length == 0
                        ? link.LastModifiedAt
                        : files.Max(x => new DateTimeOffset(x.LastWriteTimeUtc))
                };
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public MediaSourceDescriptor[] ListSources()
    {
        var imported = ListCollections().Select(collection =>
            new MediaSourceDescriptor(collection.Name, collection.Source, true, collection.ItemCount, "media"));
        var linked = ListLinkedSources().Select(link =>
            new MediaSourceDescriptor(link.Name, link.Path, true, link.ItemCount, "linked"));
        return imported.Concat(linked).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<MediaLinkedSourceDescriptor> LinkDirectoryAsync(
        string name,
        string path,
        CancellationToken ct = default)
    {
        var safeName = NormalizeSegment(name, "linked source name");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ApiValidationException("Linked directory path must be absolute.");
        var resolver = _replayInputResolver ?? throw new ApiValidationException(
            "Linked directories require host file-read authorization.");
        var resolved = resolver.Resolve(path) ?? throw new ApiValidationException(
            $"Directory '{path}' is outside the authorized read roots.");
        if (!Directory.Exists(resolved))
            throw new ApiNotFoundException($"Linked directory '{resolved}' does not exist.");
        var files = EnumerateSupportedExternalFiles(resolved);
        if (files.Length == 0)
            throw new ApiValidationException("Linked directory contains no supported images.");
        var linked = new MediaLinkedSourceDescriptor(
            safeName,
            Path.GetFullPath(resolved),
            files.Length,
            files.Sum(x => x.Length),
            files.Max(x => new DateTimeOffset(x.LastWriteTimeUtc)));

        await _mutationGate.WaitAsync(ct);
        try
        {
            var links = ReadLinkedSources()
                .Where(item => !item.Name.Equals(safeName, StringComparison.OrdinalIgnoreCase))
                .Append(linked)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await WriteLinkedSourcesAsync(links, ct);
            return linked;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> UnlinkDirectoryAsync(string name, CancellationToken ct = default)
    {
        var safeName = NormalizeSegment(name, "linked source name");
        await _mutationGate.WaitAsync(ct);
        try
        {
            var links = ReadLinkedSources();
            var next = links.Where(link => !link.Name.Equals(safeName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (next.Length == links.Length) return false;
            await WriteLinkedSourcesAsync(next, ct);
            return true;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<MediaBulkResult> BulkUpdateAsync(MediaBulkRequest request, CancellationToken ct = default)
    {
        if (request.Paths.Length == 0)
            throw new ApiValidationException("At least one media path is required.");
        var action = request.Action.Trim().ToLowerInvariant();
        if (action is not ("delete" or "move" or "relabel" or "rename"))
            throw new ApiValidationException("Media bulk action must be delete, move, relabel or rename.");
        if (action == "rename" && request.Paths.Length != 1)
            throw new ApiValidationException("Rename accepts exactly one media path.");

        var items = new List<MediaItemDescriptor>();
        var failures = new List<MediaBulkFailure>();
        await _mutationGate.WaitAsync(ct);
        try
        {
            foreach (var requestedPath in request.Paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var relative = NormalizeRelative(requestedPath);
                    var source = ResolveRelativePath(relative, allowMissingLeaf: false);
                    if (!File.Exists(source)) throw new ApiNotFoundException($"Media item '{relative}' does not exist.");
                    if (action == "delete")
                    {
                        File.Delete(source);
                        _index?.Remove(relative);
                        RemoveEmptyParents(Path.GetDirectoryName(source));
                        items.Add(DescribeDeleted(relative));
                        continue;
                    }

                    var destination = action == "rename"
                        ? RenamedDestination(source, request.TargetName)
                        : MovedDestination(relative, request.TargetCollection, request.TargetLabel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    EnsureNoReparseTraversal(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination) && !string.Equals(destination, source, _pathComparison))
                        destination = UniqueDestination(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
                    if (!string.Equals(destination, source, _pathComparison))
                        File.Move(source, destination);
                    _index?.Remove(relative);
                    RemoveEmptyParents(Path.GetDirectoryName(source));
                    var updated = Describe(destination);
                    _index?.Upsert(updated);
                    items.Add(updated);
                }
                catch (Exception ex) when (ex is ApiProblemException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(new MediaBulkFailure(requestedPath, ex.Message));
                }
            }
        }
        finally { _mutationGate.Release(); }
        return new MediaBulkResult(items.Count, items, failures);
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
            catch (IOException) { /* 单个文件被占用/消失：跳过，不影响总量统计 */ }
            catch (UnauthorizedAccessException) { /* 单个文件无权限：跳过，不影响总量统计 */ }
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

    private void EnsureIndex()
    {
        if (_index is null) return;
        _index.EnsureInitialized(() => EnumerateItems(null, null).ToArray());
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

    private static FileInfo[] EnumerateSupportedExternalFiles(string root) =>
            Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            })
            .Where(path => Supported.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .ToArray();

    private static int BitDepthOf(MatType depth) => (int)depth switch
    {
        0 or 1 => 8,
        2 or 3 => 16,
        4 or 5 => 32,
        _ => 64
    };

    private string MovedDestination(string relativePath, string? targetCollection, string? targetLabel)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ApiValidationException($"Media item '{relativePath}' does not have collection/label segments.");
        var collection = string.IsNullOrWhiteSpace(targetCollection)
            ? NormalizeSegment(segments[0], "collection")
            : NormalizeSegment(targetCollection, "collection");
        var label = string.IsNullOrWhiteSpace(targetLabel)
            ? NormalizeLabel(segments[1])
            : NormalizeLabel(targetLabel);
        var tail = string.Join('/', segments.Skip(2));
        return ResolveRelativePath($"{collection}/{label}/{tail}", allowMissingLeaf: true);
    }

    private string RenamedDestination(string source, string? targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            throw new ApiValidationException("Target name is required.");
        var safeName = NormalizeSegment(targetName, "file name");
        if (!Supported.Contains(Path.GetExtension(safeName)))
            throw new ApiValidationException("Renamed media item must keep a supported image extension.");
        return Path.Combine(Path.GetDirectoryName(source)!, safeName);
    }

    private static MediaItemDescriptor DescribeDeleted(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments.LastOrDefault() ?? relativePath;
        return new MediaItemDescriptor(
            relativePath,
            "media://" + relativePath,
            segments.FirstOrDefault() ?? "Unsorted",
            segments.Length > 1 ? segments[1] : "Unlabeled",
            name,
            Path.GetExtension(name),
            0,
            DateTimeOffset.UtcNow);
    }

    private MediaLinkedSourceDescriptor[] ReadLinkedSources()
    {
        if (!File.Exists(_linkedSourcesPath)) return [];
        try
        {
            var value = JsonSerializer.Deserialize<MediaLinkedSourceDescriptor[]>(
                File.ReadAllText(_linkedSourcesPath));
            return value ?? [];
        }
        catch (JsonException ex)
        {
            throw new ApiUnavailableException("Linked media source registry is invalid.", ex);
        }
        catch (IOException ex)
        {
            throw new ApiUnavailableException("Unable to read linked media sources.", ex);
        }
    }

    private async Task WriteLinkedSourcesAsync(
        IReadOnlyCollection<MediaLinkedSourceDescriptor> links,
        CancellationToken ct)
    {
        var temp = _linkedSourcesPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(links, new JsonSerializerOptions { WriteIndented = true }),
                ct);
            File.Move(temp, _linkedSourcesPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task<bool> FilesEqualAsync(string left, string right, CancellationToken ct)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        await using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        await using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[64 * 1024];
        while (true)
        {
            var leftCount = await leftStream.ReadAsync(leftBuffer, ct);
            var rightCount = await rightStream.ReadAsync(rightBuffer, ct);
            if (leftCount != rightCount || !leftBuffer.AsSpan(0, leftCount).SequenceEqual(rightBuffer.AsSpan(0, rightCount)))
                return false;
            if (leftCount == 0) return true;
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
