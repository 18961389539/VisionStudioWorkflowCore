using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageViewer.Services
{
    /// <summary>
    /// Writes a file completely before replacing the destination. The temporary file is
    /// created beside the destination so a successful commit stays on the same volume.
    /// </summary>
    internal static class ImageViewerAtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void WriteAllText(string filePath, string contents, Encoding? encoding = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(contents);

            string fullPath = Path.GetFullPath(filePath);
            string temporaryPath = CreateTemporaryPath(fullPath);
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                Commit(temporaryPath, fullPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        public static async Task WriteAllTextAsync(string filePath, string contents, Encoding? encoding = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(contents);

            string fullPath = Path.GetFullPath(filePath);
            string temporaryPath = CreateTemporaryPath(fullPath);
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.SequentialScan | FileOptions.Asynchronous))
                await using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true))
                {
                    await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Commit(temporaryPath, fullPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        public static string CreateTemporaryPath(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            string fullPath = Path.GetFullPath(filePath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        }

        public static void Commit(string temporaryPath, string destinationPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string fullDestinationPath = Path.GetFullPath(destinationPath);
            if (File.Exists(fullDestinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, fullDestinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    // Fall through to Move for filesystems without Replace support.
                }
                catch (NotSupportedException)
                {
                    // Fall through to Move for filesystems without Replace support.
                }
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }

        public static void TryDelete(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Cleanup must never hide the original write or commit failure.
            }
        }
    }
}
