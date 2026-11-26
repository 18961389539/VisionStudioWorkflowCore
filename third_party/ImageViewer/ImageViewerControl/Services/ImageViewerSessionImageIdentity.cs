using System.IO;
using System.Security.Cryptography;

namespace ImageViewer.Services;

internal static class ImageViewerSessionImageIdentity
{
    internal static string? Normalize(string? digest, string? imagePath)
    {
        if (digest == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(imagePath) || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The session contains an invalid image SHA-256.");
        }
        return digest.ToUpperInvariant();
    }

    internal static void Verify(string? expected, string actual, string path)
    {
        if (expected != null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImageViewerSessionImageMismatchException(path);
        }
    }

    internal static async Task<string?> TryReadAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Older callers may save references before the image becomes available.
            return null;
        }
    }
}

internal sealed class ImageViewerSessionImageMismatchException(string path)
    : IOException($"The image file does not match the SHA-256 recorded by the session: {path}");
