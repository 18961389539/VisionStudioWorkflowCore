using System.IO;
using System.Security.Cryptography;

namespace ImageViewer.Services;

/// <summary>Stores images needed by standalone sessions beside the session, outside the package cache.</summary>
internal static class ImageViewerSessionAssets
{
    private const string AssetDirectoryName = "ImageViewer.assets";

    public static bool IsManagedAsset(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(path);
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), AssetDirectoryName, StringComparison.OrdinalIgnoreCase) &&
            fileName.Length == 64 && fileName.All(Uri.IsHexDigit);
    }

    public static async Task<string> SaveAsync(string sessionPath, string sourcePath, string? expectedSha256, CancellationToken cancellationToken)
    {
        string sessionDirectory = Path.GetDirectoryName(Path.GetFullPath(sessionPath))!;
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(hash);
        ImageViewerSessionImageIdentity.Verify(expectedSha256, sha256, sourcePath);
        string fileName = sha256 + Path.GetExtension(sourcePath).ToLowerInvariant();
        string relativePath = Path.Combine(AssetDirectoryName, fileName);
        string destination = Path.Combine(sessionDirectory, relativePath);

        if (string.Equals(Path.GetFullPath(sourcePath), destination, StringComparison.OrdinalIgnoreCase) ||
            await MatchesAsync(destination, hash, input.Length, cancellationToken).ConfigureAwait(false))
        {
            return relativePath;
        }

        string temporary = ImageViewerAtomicFile.CreateTemporaryPath(destination);
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ImageViewerAtomicFile.Commit(temporary, destination);
            return relativePath;
        }
        finally
        {
            ImageViewerAtomicFile.TryDelete(temporary);
        }
    }

    private static async Task<bool> MatchesAsync(string path, byte[] hash, long length, CancellationToken cancellationToken)
    {
        try
        {
            await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return existing.Length == length &&
                (await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(hash);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
