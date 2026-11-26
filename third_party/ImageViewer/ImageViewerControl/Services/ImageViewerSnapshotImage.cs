using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services;

internal static class ImageViewerSnapshotImage
{
    private const string RecoveryImageDirectoryName = "RecoveryImages";
    private static readonly ConditionalWeakTable<BitmapSource, ImageIdentity> RecoveryIdentities = new();

    public static (string? Path, BitmapSource? Bitmap, string? Sha256) Capture(string? path, Func<BitmapSource?> getBitmap)
    {
        if (!string.IsNullOrWhiteSpace(path) && !IsRecoveryAsset(path) && File.Exists(path))
        {
            return (path, null, Controls.ImageViewerImageSourceRegistry.GetSha256(getBitmap()));
        }

        BitmapSource? bitmap = getBitmap();
        if (bitmap == null)
        {
            return (path, null, null);
        }

        // The persistence worker must neither access dispatcher-bound pixels nor see edits made after capture.
        return (null, bitmap.IsFrozen ? bitmap : (BitmapSource)bitmap.GetCurrentValueAsFrozen(), null);
    }

    public static bool IsRecoveryAsset(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), RecoveryImageDirectoryName, StringComparison.OrdinalIgnoreCase)
            && IsOwnedImageName(Path.GetFileName(path));
    }

    public static async Task<(string RelativePath, bool Created, string Sha256)> SaveRecoveryImageAsync(
        string sessionPath, BitmapSource bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = GetRecoveryImageDirectory(sessionPath);
        var identity = RecoveryIdentities.GetValue(bitmap, static _ => new ImageIdentity());
        string destination = Path.Combine(directory, identity.FileName);
        bool created = !File.Exists(destination);
        string relativePath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sessionPath))!, destination);
        if (created)
        {
            string temporary = ImageViewerAtomicFile.CreateTemporaryPath(destination);
            try
            {
                WritePng(temporary, bitmap, cancellationToken);
                string sha256 = (await ImageViewerSessionImageIdentity.TryReadAsync(temporary, cancellationToken).ConfigureAwait(false))
                    ?? throw new FileNotFoundException("The encoded recovery image is missing.", temporary);
                cancellationToken.ThrowIfCancellationRequested();
                ImageViewerAtomicFile.Commit(temporary, destination);
                identity.LastSaved = new(destination, sha256);
                return (relativePath, true, sha256);
            }
            finally
            {
                ImageViewerAtomicFile.TryDelete(temporary);
            }
        }

        SavedImage? saved = identity.LastSaved;
        if (saved?.Path == destination)
        {
            return (relativePath, false, saved.Sha256);
        }
        string existingSha256 = (await ImageViewerSessionImageIdentity.TryReadAsync(destination, cancellationToken).ConfigureAwait(false))
            ?? throw new FileNotFoundException("The recovery image is missing.", destination);
        identity.LastSaved = new(destination, existingSha256);
        return (relativePath, false, existingSha256);
    }

    public static void WritePng(string path, BitmapSource bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!bitmap.IsFrozen)
        {
            throw new ArgumentException("An image in a persistence snapshot must be frozen.", nameof(bitmap));
        }
        ImageViewerImageDecoder.EnsureWithinLimits(bitmap.PixelWidth, bitmap.PixelHeight, ImageViewerImageDecoder.DefaultMaxPixelCount);

        PixelFormat format = bitmap.Format;
        if (format == PixelFormats.Pbgra32 || format == PixelFormats.Bgr32)
        {
            bitmap = new FormatConvertedBitmap(bitmap, format == PixelFormats.Pbgra32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, 0);
        }
        else if (format != PixelFormats.Gray16 && format != PixelFormats.Gray8 &&
            format != PixelFormats.Gray4 && format != PixelFormats.Gray2 && format != PixelFormats.BlackWhite &&
            format != PixelFormats.Indexed1 && format != PixelFormats.Indexed2 &&
            format != PixelFormats.Indexed4 && format != PixelFormats.Indexed8 &&
            format != PixelFormats.Bgr24 && format != PixelFormats.Rgb24 && format != PixelFormats.Bgra32)
        {
            throw new NotSupportedException($"The bitmap format {format} cannot be saved as a session image without changing its pixels.");
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Removes only generated sidecars owned by this snapshot. Call after committing or deleting the session file.</summary>
    public static void PruneRecoveryImages(string sessionPath, string? retainedImagePath = null)
    {
        string directory = GetRecoveryImageDirectory(sessionPath);
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(Path.GetDirectoryName(directory)!) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            string? retainedName = null;
            if (!string.IsNullOrWhiteSpace(retainedImagePath))
            {
                string retained = Path.GetFullPath(retainedImagePath, Path.GetDirectoryName(Path.GetFullPath(sessionPath))!);
                if (string.Equals(Path.GetDirectoryName(retained), directory, StringComparison.OrdinalIgnoreCase))
                {
                    retainedName = Path.GetFileName(retained);
                }
            }
            foreach (string path in Directory.EnumerateFiles(directory, "*.png"))
            {
                string name = Path.GetFileName(path);
                if (IsOwnedImageName(name) && !string.Equals(name, retainedName, StringComparison.OrdinalIgnoreCase))
                {
                    ImageViewerAtomicFile.TryDelete(path);
                }
            }
            if (retainedName == null && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Cleaning up an obsolete image must not turn a successful save into a failure.
        }
    }

    private static string GetRecoveryImageDirectory(string sessionPath)
    {
        string fullPath = Path.GetFullPath(sessionPath);
        return Path.Combine(Path.GetDirectoryName(fullPath)!, RecoveryImageDirectoryName, Path.GetFileName(fullPath));
    }

    private static bool IsOwnedImageName(string name) =>
        string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);

    private sealed class ImageIdentity
    {
        public string FileName { get; } = $"{Guid.NewGuid():N}.png";
        public volatile SavedImage? LastSaved;
    }

    private sealed record SavedImage(string Path, string Sha256);
}
