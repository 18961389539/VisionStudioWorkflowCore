using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageViewer.Services
{
    /// <summary>
    /// Stages a related set of files before replacing any destination, and restores the
    /// previous files if a later replacement fails. This is rollback protection, not a
    /// filesystem-wide crash-atomic transaction.
    /// </summary>
    internal static class ImageViewerExportArtifactSet
    {
        private static readonly SemaphoreSlim CommitGate = new(1, 1);

        public static Task WriteAllTextAsync(
            IReadOnlyDictionary<string, string> artifacts,
            CancellationToken cancellationToken = default) =>
            WriteAllBytesAsync(
                artifacts?.ToDictionary(pair => pair.Key, pair => EncodeUtf8WithBom(pair.Value), StringComparer.OrdinalIgnoreCase)
                    ?? throw new ArgumentNullException(nameof(artifacts)),
                cancellationToken: cancellationToken);

        internal static string ComputeSha256Utf8Text(string contents) =>
            Convert.ToHexString(SHA256.HashData(EncodeUtf8WithBom(contents))).ToLowerInvariant();

        internal static byte[] EncodeUtf8WithBom(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            byte[] preamble = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes(value);
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            return bytes;
        }

        public static async Task WriteAllBytesAsync(
            IReadOnlyDictionary<string, byte[]> artifacts,
            IImageViewerExportArtifactFileSystem? fileSystem = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(artifacts);
            if (artifacts.Count == 0)
            {
                return;
            }

            fileSystem ??= PhysicalImageViewerExportArtifactFileSystem.Instance;
            var entries = artifacts.Select(pair =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                string target = Path.GetFullPath(pair.Key);
                string directory = Path.GetDirectoryName(target) ?? Directory.GetCurrentDirectory();
                return new Entry(target, directory, pair.Value);
            }).ToList();

            if (entries.Select(entry => entry.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            {
                throw new ArgumentException("Artifact paths must be unique.", nameof(artifacts));
            }

            await CommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool commitSucceeded = false;
            try
            {
                foreach (Entry entry in entries)
                {
                    fileSystem.CreateDirectory(entry.DirectoryPath);
                    entry.StagedPath = CreateSiblingPath(entry.TargetPath, "stage");
                    await fileSystem.WriteAllBytesAsync(entry.StagedPath, entry.Contents, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (Entry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entry.HadOriginal = fileSystem.FileExists(entry.TargetPath);
                    if (entry.HadOriginal)
                    {
                        entry.BackupPath = CreateSiblingPath(entry.TargetPath, "backup");
                        fileSystem.Replace(entry.StagedPath!, entry.TargetPath, entry.BackupPath);
                    }
                    else
                    {
                        fileSystem.Move(entry.StagedPath!, entry.TargetPath);
                    }

                    entry.Committed = true;
                }

                commitSucceeded = true;
            }
            catch (Exception commitException)
            {
                Exception? rollbackException = Rollback(entries, fileSystem);
                if (rollbackException != null)
                {
                    throw new AggregateException("Artifact-set commit failed and rollback was incomplete.", commitException, rollbackException);
                }

                throw;
            }
            finally
            {
                foreach (Entry entry in entries)
                {
                    fileSystem.TryDelete(entry.StagedPath);
                    if (commitSucceeded)
                    {
                        fileSystem.TryDelete(entry.BackupPath);
                    }
                }

                CommitGate.Release();
            }
        }

        private static Exception? Rollback(IReadOnlyList<Entry> entries, IImageViewerExportArtifactFileSystem fileSystem)
        {
            List<Exception>? failures = null;
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                Entry entry = entries[index];
                if (!entry.Committed)
                {
                    continue;
                }

                try
                {
                    if (entry.HadOriginal)
                    {
                        fileSystem.Replace(entry.BackupPath!, entry.TargetPath, backupPath: null);
                        entry.BackupPath = null;
                    }
                    else
                    {
                        fileSystem.Delete(entry.TargetPath);
                    }

                    entry.Committed = false;
                }
                catch (Exception exception)
                {
                    failures ??= new List<Exception>();
                    failures.Add(exception);
                }
            }

            return failures is null ? null : new AggregateException("One or more artifact files could not be rolled back.", failures);
        }

        private static string CreateSiblingPath(string targetPath, string purpose) =>
            Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.{purpose}");

        private sealed class Entry(string targetPath, string directoryPath, byte[] contents)
        {
            public string TargetPath { get; } = targetPath;
            public string DirectoryPath { get; } = directoryPath;
            public byte[] Contents { get; } = contents;
            public string? StagedPath { get; set; }
            public string? BackupPath { get; set; }
            public bool HadOriginal { get; set; }
            public bool Committed { get; set; }
        }
    }

    internal interface IImageViewerExportArtifactFileSystem
    {
        void CreateDirectory(string path);
        Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken);
        bool FileExists(string path);
        void Move(string sourcePath, string destinationPath);
        void Replace(string sourcePath, string destinationPath, string? backupPath);
        void Delete(string path);
        void TryDelete(string? path);
    }

    internal sealed class PhysicalImageViewerExportArtifactFileSystem : IImageViewerExportArtifactFileSystem
    {
        public static PhysicalImageViewerExportArtifactFileSystem Instance { get; } = new();
        private PhysicalImageViewerExportArtifactFileSystem() { }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        public bool FileExists(string path) => File.Exists(path);
        public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);
        public void Replace(string sourcePath, string destinationPath, string? backupPath) => File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
        public void Delete(string path) => File.Delete(path);
        public void TryDelete(string? path) => ImageViewerAtomicFile.TryDelete(path);
    }
}
