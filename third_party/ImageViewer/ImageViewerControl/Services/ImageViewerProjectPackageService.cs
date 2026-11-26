using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    public sealed class ImageViewerProjectPackageService : IImageViewerProjectPackageService
    {
        private const string SessionEntryName = "session.ivsession";
        private const string AssetFolderName = "assets";
        private const int MaximumArchiveEntryCount = 1_024;
        private const int MaximumArchiveEntryNameLength = 512;
        private const long MaximumSessionEntryUncompressedBytes = 8L * 1024 * 1024;
        private const long MaximumArchiveEntryUncompressedBytes = 256L * 1024 * 1024;
        private const long MaximumArchiveUncompressedBytes = 512L * 1024 * 1024;
        private static readonly DateTime ZipEpochUtc = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly IImageViewerSessionService _sessionService;
        private readonly IImageViewerSessionStoragePolicy _sessionStoragePolicy;

        public ImageViewerProjectPackageService(IImageViewerSessionService sessionService, IImageViewerSessionStoragePolicy sessionStoragePolicy)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _sessionStoragePolicy = sessionStoragePolicy ?? throw new ArgumentNullException(nameof(sessionStoragePolicy));
        }

        public async Task ExportAsync(string packagePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            cancellationToken.ThrowIfCancellationRequested();

            string fullPackagePath = Path.GetFullPath(packagePath);
            string? directory = Path.GetDirectoryName(fullPackagePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPackagePath = Path.Combine(
                directory ?? Directory.GetCurrentDirectory(),
                $".{Path.GetFileName(fullPackagePath)}.{Guid.NewGuid():N}.tmp");

            string? temporaryImagePath = null;
            try
            {
                string? imagePath = snapshot.ImagePath;
                string? packagedImagePath = null;
                using FileStream? cacheLease = snapshot.InMemoryImage == null
                    ? await Task.Run(() => ImageViewerProjectPackageCache.TryLeaseAsset(imagePath, cancellationToken),
                        cancellationToken).ConfigureAwait(false)
                    : null;
                if (snapshot.InMemoryImage is { } bitmap)
                {
                    string encodedPath = temporaryPackagePath + ".png";
                    temporaryImagePath = encodedPath;
                    await Task.Run(() => ImageViewerSnapshotImage.WritePng(encodedPath, bitmap, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    imagePath = encodedPath;
                    packagedImagePath = $"{AssetFolderName}/image.png";
                }
                else if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                {
                    packagedImagePath = $"{AssetFolderName}/{Path.GetFileName(imagePath)}";
                }

                if (packagedImagePath == null && snapshot.ImageSha256 != null)
                {
                    throw new FileNotFoundException("The original image is missing.", imagePath);
                }

                // Packaging compresses the whole image, so it runs on a background thread:
                // doing it inline would freeze the UI for as long as Optimal compression takes.
                await Task.Run(
                    () => WritePackageAsync(temporaryPackagePath, Path.GetFileNameWithoutExtension(fullPackagePath),
                        snapshot, pluginRegistry, imagePath, packagedImagePath, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                ImageViewerAtomicFile.Commit(temporaryPackagePath, fullPackagePath);
            }
            finally
            {
                ImageViewerAtomicFile.TryDelete(temporaryImagePath);
                ImageViewerAtomicFile.TryDelete(temporaryPackagePath);
            }
        }

        /// <summary>
        /// 写出包内容（会话条目 + 可选图片资产）。
        /// Chinese: 由 <see cref="ExportAsync"/> 在后台线程调用；图片用异步流拷贝，因此超大图导出也能响应取消。
        /// English: Runs on a background thread; the image asset is copied with an async stream so large exports stay cancellable.
        /// </summary>
        private async Task WritePackageAsync(
            string temporaryPackagePath,
            string sessionName,
            ImageViewerPersistenceSnapshot snapshot,
            RoiPluginRegistry pluginRegistry,
            string? imagePath,
            string? packagedImagePath,
            CancellationToken cancellationToken)
        {
            using var archive = ZipFile.Open(temporaryPackagePath, ZipArchiveMode.Create);

            string? sha256 = null;
            if (imagePath != null && packagedImagePath != null)
            {
                sha256 = await AddFileEntryAsync(archive, packagedImagePath, imagePath, cancellationToken).ConfigureAwait(false);
                if (snapshot.InMemoryImage == null)
                {
                    ImageViewerSessionImageIdentity.Verify(snapshot.ImageSha256, sha256, imagePath);
                }
            }

            // The digest covers exactly the bytes copied into the archive.
            string sessionJson = _sessionService.SerializeSession(sessionName,
                snapshot with { ImagePath = packagedImagePath, InMemoryImage = null, ImageSha256 = sha256 }, pluginRegistry);
            if (Encoding.UTF8.GetByteCount(sessionJson) > MaximumSessionEntryUncompressedBytes)
            {
                throw new InvalidDataException($"The session entry exceeds the maximum uncompressed size of {MaximumSessionEntryUncompressedBytes} bytes.");
            }

            ZipArchiveEntry sessionEntry = archive.CreateEntry(SessionEntryName, CompressionLevel.Optimal);
            await using (Stream sessionStream = sessionEntry.Open())
            {
                await using var writer = new StreamWriter(sessionStream);
                await writer.WriteAsync(sessionJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<string> AddFileEntryAsync(
            ZipArchive archive,
            string entryName,
            string sourcePath,
            CancellationToken cancellationToken)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            // ZipArchiveEntry rejects timestamps outside the ZIP range; keep the source timestamp only when representable.
            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
            if (lastWriteUtc >= ZipEpochUtc)
            {
                entry.LastWriteTime = lastWriteUtc;
            }

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > MaximumArchiveEntryUncompressedBytes)
            {
                throw new InvalidDataException($"The image asset exceeds the maximum uncompressed size of {MaximumArchiveEntryUncompressedBytes} bytes.");
            }

            await using Stream target = entry.Open();
            using var hash = SHA256.Create();
            await using var hashingTarget = new CryptoStream(target, hash, CryptoStreamMode.Write, leaveOpen: true);
            await source.CopyToAsync(hashingTarget, 81920, cancellationToken).ConfigureAwait(false);
            await hashingTarget.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash.Hash!);
        }

        public async Task<ImageViewerSessionData> LoadAsync(string packagePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            cancellationToken.ThrowIfCancellationRequested();

            string fullPackagePath = Path.GetFullPath(packagePath);
            await using FileStream packageStream = File.OpenRead(fullPackagePath);
            byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
            packageStream.Position = 0;
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            (ZipArchiveEntry sessionEntry, IReadOnlySet<string> assetFileNames) = ValidateArchive(archive);

            using var sessionStream = sessionEntry.Open();
            using var reader = new StreamReader(sessionStream);
            string sessionJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            ValidateSessionImageReference(sessionJson, assetFileNames);

            string cacheRoot = Path.GetFullPath(_sessionStoragePolicy.ProjectPackageCacheDirectory);
            string entryDirectory = ImageViewerProjectPackageCache.GetDirectory(cacheRoot, hash);
            FileStream lease = await PrepareCachedPackageAsync(archive, cacheRoot, entryDirectory, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageViewerSessionData session = _sessionService.LoadFromJson(sessionJson, pluginRegistry, entryDirectory);
                ImageViewerProjectPackageCache.AttachSessionLease(session, lease);
                return session;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private async Task<FileStream> PrepareCachedPackageAsync(
            ZipArchive archive, string cacheRoot, string entryDirectory, CancellationToken cancellationToken)
        {
            using FileStream cacheLock = await ImageViewerProjectPackageCache.LockAsync(cacheRoot, cancellationToken).ConfigureAwait(false);
            if (!IsCompleteExtraction(archive, entryDirectory))
            {
                string staging = Path.Combine(cacheRoot, $"staging-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                try
                {
                    long bytes = await ExtractArchiveEntriesAsync(archive, staging, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ImageViewerProjectPackageCache.Complete(staging, bytes);
                    if (Directory.Exists(entryDirectory))
                    {
                        ImageViewerProjectPackageCache.RemoveEntry(entryDirectory);
                    }
                    Directory.Move(staging, entryDirectory);
                }
                finally
                {
                    if (Directory.Exists(staging))
                    {
                        ImageViewerProjectPackageCache.RemoveEntry(staging);
                    }
                }
            }

            ImageViewerProjectPackageCache.Trim(cacheRoot, entryDirectory, _sessionStoragePolicy.AutoSaveDirectory,
                _sessionStoragePolicy.ProjectPackageCacheBudgetBytes);
            return ImageViewerProjectPackageCache.LeaseEntry(entryDirectory);
        }

        private static (ZipArchiveEntry SessionEntry, IReadOnlySet<string> AssetFileNames) ValidateArchive(ZipArchive archive)
        {
            if (archive.Entries.Count > MaximumArchiveEntryCount)
            {
                throw new InvalidDataException($"The project package contains more than {MaximumArchiveEntryCount} entries.");
            }

            ZipArchiveEntry? sessionEntry = null;
            var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalUncompressedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryName = entry.FullName;
                string normalizedName = NormalizeArchiveEntryName(entryName);
                if (!normalizedNames.Add(normalizedName))
                {
                    throw new InvalidDataException($"The project package contains duplicate entry '{entryName}'.");
                }

                bool isSessionEntry = string.Equals(normalizedName, SessionEntryName, StringComparison.OrdinalIgnoreCase);
                if (isSessionEntry)
                {
                    if (entryName.EndsWith('/'))
                    {
                        throw new InvalidDataException("The project package session entry must be a file.");
                    }

                    if (sessionEntry is not null)
                    {
                        throw new InvalidDataException("The project package contains more than one session file.");
                    }

                    sessionEntry = entry;
                    if (entry.Length > MaximumSessionEntryUncompressedBytes)
                    {
                        throw new InvalidDataException($"The session entry exceeds the maximum uncompressed size of {MaximumSessionEntryUncompressedBytes} bytes.");
                    }
                }
                else
                {
                    ValidateAssetEntryName(normalizedName, entryName.EndsWith('/'));
                    if (!entryName.EndsWith('/'))
                    {
                        fileNames.Add(normalizedName);
                    }
                }

                if (entry.Length > MaximumArchiveEntryUncompressedBytes)
                {
                    throw new InvalidDataException($"Archive entry '{entryName}' exceeds the maximum uncompressed size.");
                }

                try
                {
                    totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("The project package has an invalid uncompressed size.", exception);
                }

                if (totalUncompressedBytes > MaximumArchiveUncompressedBytes)
                {
                    throw new InvalidDataException("The project package exceeds the maximum total uncompressed size.");
                }
            }

            foreach (string normalizedName in normalizedNames)
            {
                int separatorIndex = normalizedName.IndexOf('/');
                while (separatorIndex > 0)
                {
                    if (fileNames.Contains(normalizedName[..separatorIndex]))
                    {
                        throw new InvalidDataException($"The project package contains a file path conflict at '{normalizedName}'.");
                    }

                    separatorIndex = normalizedName.IndexOf('/', separatorIndex + 1);
                }
            }

            return (sessionEntry ?? throw new InvalidDataException("The project package does not contain a session file."), fileNames);
        }

        private static bool IsCompleteExtraction(ZipArchive archive, string directory)
        {
            if (!File.Exists(ImageViewerProjectPackageCache.MarkerPath(directory)) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            foreach (ZipArchiveEntry entry in GetAssetEntries(archive))
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }
                var file = new FileInfo(Path.Combine(directory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.Exists || file.Length != entry.Length || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static IEnumerable<ZipArchiveEntry> GetAssetEntries(ZipArchive archive) =>
            archive.Entries.Where(entry => !string.Equals(entry.FullName, SessionEntryName, StringComparison.OrdinalIgnoreCase));

        private static async Task<long> ExtractArchiveEntriesAsync(ZipArchive archive, string destinationDirectory, CancellationToken cancellationToken)
        {
            string rootPath = Path.GetFullPath(destinationDirectory);
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar))
            {
                rootPath += Path.DirectorySeparatorChar;
            }

            long totalBytes = 0;
            foreach (ZipArchiveEntry entry in GetAssetEntries(archive))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destinationPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unsafe archive entry path '{entry.FullName}'.");
                }

                string? destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                await using Stream input = entry.Open();
                await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (output.Length != entry.Length)
                {
                    throw new InvalidDataException($"Unexpected size for archive entry '{entry.FullName}'.");
                }
                totalBytes += output.Length;
            }
            return totalBytes;
        }

        private static string NormalizeArchiveEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName) || entryName.Length > MaximumArchiveEntryNameLength)
            {
                throw new InvalidDataException("The project package contains an invalid or oversized entry name.");
            }

            if (entryName.Contains('\\') || entryName.StartsWith('/') || Path.IsPathRooted(entryName) || (entryName.Length > 1 && entryName[1] == ':'))
            {
                throw new InvalidDataException($"Unsafe archive entry path '{entryName}'.");
            }

            bool isDirectory = entryName.EndsWith('/');
            string normalizedName = isDirectory ? entryName[..^1] : entryName;
            if (normalizedName.Length == 0)
            {
                throw new InvalidDataException("The project package contains an empty entry path.");
            }

            string[] segments = normalizedName.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(char.IsControl))
                {
                    throw new InvalidDataException($"Unsafe archive entry path '{entryName}'.");
                }
            }

            return normalizedName;
        }

        private static void ValidateAssetEntryName(string normalizedName, bool isDirectory)
        {
            string assetPrefix = AssetFolderName + "/";
            if (string.Equals(normalizedName, AssetFolderName, StringComparison.OrdinalIgnoreCase))
            {
                if (!isDirectory)
                {
                    throw new InvalidDataException($"The '{AssetFolderName}' archive entry must be a directory.");
                }

                return;
            }

            if (!normalizedName.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Project package entry '{normalizedName}' is outside the '{AssetFolderName}/' directory.");
            }
        }

        /// <summary>
        /// 校验会话中的图像引用。
        /// Chinese: 反序列化是大小写不敏感的（PropertyNameCaseInsensitive = true），而 <c>JsonElement.TryGetProperty</c>
        /// 是大小写敏感的——只查 "ImagePath" 会让 "imagepath" 之类的变体绕过 assets/ 限制、又被反序列化填充。
        /// 因此这里枚举根对象全部属性：发现任何大小写变体直接判非法（大小写游戏本身即恶意信号）；
        /// 并要求引用的资产真实存在于包内（fileNames 已在 ValidateArchive 中收集）。
        /// English: Deserialization is case-insensitive while JsonElement lookup is not, so every root property is
        /// enumerated and any case variant is rejected; the referenced asset must also exist inside the archive.
        /// </summary>
        private static void ValidateSessionImageReference(string sessionJson, IReadOnlySet<string> assetFileNames)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(sessionJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The project package session must be a JSON object.");
                }

                string? imagePath = null;
                bool hasImagePath = false;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "ImagePath", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(property.Name, "ImagePath", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The project package session contains a case-variant 'ImagePath' property.");
                    }

                    hasImagePath = true;
                    if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        continue;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("The project package session contains an invalid image path.");
                    }

                    imagePath = property.Value.GetString();
                }

                if (!hasImagePath || string.IsNullOrWhiteSpace(imagePath))
                {
                    return;
                }

                if (imagePath.EndsWith('/'))
                {
                    throw new InvalidDataException("The project package session contains an image path that points to a directory.");
                }

                string normalizedPath = NormalizeArchiveEntryName(imagePath);
                ValidateAssetEntryName(normalizedPath, isDirectory: false);

                if (!assetFileNames.Contains(normalizedPath))
                {
                    throw new InvalidDataException($"The project package session references image '{imagePath}' which is not part of the package.");
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The project package session is not valid JSON.", exception);
            }
        }

    }
}
