using System.IO;
using System.Windows.Media.Imaging;
using ImageViewer.Localization;

namespace ImageViewer.Services;

public sealed record VolumeImportEstimate(int Width, int Height, long DecodedBytes, long EstimatedBytes)
{
    public bool ExceedsBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        return EstimatedBytes > budgetBytes;
    }
}

/// <summary>A checked import that verifies each file before decoding its stored pixel grid.</summary>
public sealed class VolumeImportPlan
{
    // Limit overlap when decoding large planes, since a codec can use additional temporary buffers.
    private const long MaxConcurrentSliceEstimate = 32L * 1024 * 1024;
    private readonly VolumeImportSlice[] _slices;

    internal VolumeImportPlan(VolumeImportEstimate estimate, VolumeImportSlice[] slices)
    {
        Estimate = estimate;
        _slices = slices;
    }

    public VolumeImportEstimate Estimate { get; }

    public int SliceCount => _slices.Length;

    /// <summary>Decodes the specified slice only if the file still matches preflight.</summary>
    public Task<BitmapSource> LoadSliceAsync(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _slices.Length);
        return Task.Run(() => LoadSlice(index, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Loads verified slices in preflight order. Progress reports the number of completed slices.
    /// </summary>
    public async Task<BitmapSource[]> LoadAllSlicesAsync(
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var bitmaps = new BitmapSource[_slices.Length];
        var progressGate = new object();
        int completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, _slices.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = _slices.All(slice => slice.EstimatedBytes <= MaxConcurrentSliceEstimate) ? 2 : 1,
            TaskScheduler = TaskScheduler.Default,
            CancellationToken = cancellationToken
        }, (index, token) =>
        {
            bitmaps[index] = LoadSlice(index, token);
            if (progress != null)
            {
                lock (progressGate)
                {
                    progress.Report(++completed);
                }
            }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return bitmaps;
    }

    private BitmapSource LoadSlice(int index, CancellationToken cancellationToken)
    {
        VolumeImportSlice slice = _slices[index];
        BitmapSource bitmap;
        try
        {
            bitmap = ImageViewerImageDecoder.LoadVerifiedVolumeSlice(slice.Path, slice.Sha256, slice.Header, cancellationToken);
        }
        catch (ImageViewerSessionImageMismatchException ex)
        {
            throw new InvalidDataException(UiText.Format("VolumePreflightChangedSlice", index + 1, Path.GetFileName(slice.Path)), ex);
        }
        catch (Exception ex) when (VolumeImportPreflight.IsSliceReadError(ex))
        {
            throw VolumeImportPreflight.CreateReadError(index, slice.Path, ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap.PixelWidth != Estimate.Width || bitmap.PixelHeight != Estimate.Height)
        {
            throw new InvalidDataException(UiText.Format("VolumePreflightDimensionMismatch",
                index + 1, Path.GetFileName(slice.Path), bitmap.PixelWidth, bitmap.PixelHeight, Estimate.Width, Estimate.Height));
        }
        return bitmap;
    }
}

internal sealed record VolumeImportSlice(
    string Path, string Sha256, ImageViewerImageDecoder.ImageHeader Header, long EstimatedBytes);

/// <summary>Checks stored slice dimensions and estimates memory without decoding slice pixels.</summary>
public static class VolumeImportPreflight
{
    public static Task<VolumeImportEstimate> InspectAsync(
        IReadOnlyList<string> orderedPaths, CancellationToken cancellationToken = default)
    {
        string[] paths = CopyPaths(orderedPaths);
        return Task.Run(() => Inspect(paths, null, null, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Checks the stored slice grid and hashes each file so subsequent decoding can verify the same bytes.
    /// Progress counts inspected slices on a 0–100 scale.
    /// </summary>
    public static Task<VolumeImportPlan> PrepareAsync(IReadOnlyList<string> orderedPaths,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string[] paths = CopyPaths(orderedPaths);
        return Task.Run(() =>
        {
            var slices = new VolumeImportSlice[paths.Length];
            VolumeImportEstimate estimate = Inspect(paths, slices, progress, cancellationToken);
            return new VolumeImportPlan(estimate, slices);
        }, cancellationToken);
    }

    private static string[] CopyPaths(IReadOnlyList<string> orderedPaths)
    {
        ArgumentNullException.ThrowIfNull(orderedPaths);
        string[] paths = [.. orderedPaths];
        if (paths.Length < 2)
        {
            throw new ArgumentException("A volume requires at least two slices.", nameof(orderedPaths));
        }

        return paths;
    }

    private static VolumeImportEstimate Inspect(string[] paths, VolumeImportSlice[]? verifiedSlices,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int width = 0;
        int height = 0;
        long decodedBytes = 0;
        long estimatedBytes = 0;
        for (int index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[index];
            ImageViewerImageDecoder.ImageHeader header;
            string? sha256;
            try
            {
                path = Path.GetFullPath(path);
                (header, sha256) = ImageViewerImageDecoder.InspectFile(path, verifiedSlices != null, cancellationToken);
            }
            catch (Exception ex) when (IsSliceReadError(ex))
            {
                throw CreateReadError(index, path, ex);
            }

            if (index == 0)
            {
                width = header.PixelWidth;
                height = header.PixelHeight;
            }
            else if (header.PixelWidth != width || header.PixelHeight != height)
            {
                throw new InvalidDataException(UiText.Format("VolumePreflightDimensionMismatch",
                    index + 1, Path.GetFileName(path), header.PixelWidth, header.PixelHeight, width, height));
            }

            // Align rows to 32 bits and budget at least one 32-bit pixel per voxel for display.
            long stride = ((long)width * header.BitsPerPixel + 31) / 32 * 4;
            long sliceEstimate = Math.Max(stride, (long)width * 4) * height;
            decodedBytes = checked(decodedBytes + stride * height);
            estimatedBytes = checked(estimatedBytes + sliceEstimate);
            if (verifiedSlices != null)
            {
                verifiedSlices[index] = new VolumeImportSlice(path, sha256!, header, sliceEstimate);
            }
            progress?.Report((index + 1) * 100d / paths.Length);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new VolumeImportEstimate(width, height, decodedBytes, estimatedBytes);
    }

    internal static bool IsSliceReadError(Exception ex) => ex is IOException or InvalidDataException or
        NotSupportedException or InvalidOperationException or FormatException or UnauthorizedAccessException or ArgumentException;

    internal static InvalidDataException CreateReadError(int index, string path, Exception ex) =>
        new(UiText.Format("VolumePreflightInvalidSlice", index + 1, Path.GetFileName(path), ex.Message), ex);
}
