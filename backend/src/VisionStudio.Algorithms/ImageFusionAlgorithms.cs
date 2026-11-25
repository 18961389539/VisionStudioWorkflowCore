using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class ImageFusionAlgorithms
{
    internal static Mat Fuse(List<VisionImage> images, string mode, CancellationToken cancellationToken) => mode switch
    {
        "min" => Fold(images, (left, right, output) => Cv2.Min(left, right, output)),
        "max" => Fold(images, (left, right, output) => Cv2.Max(left, right, output)),
        "median" => Median(images, cancellationToken),
        _ => Mean(images)
    };

    private static Mat Mean(List<VisionImage> images)
    {
        using var accumulator = new Mat();
        if (images[0].Mat.Depth() == MatType.CV_64F || images[0].Mat.Depth() == MatType.CV_32F ||
            images[0].Mat.Depth() == MatType.CV_32S)
        {
            var weight = 1.0 / images.Count;
            images[0].Mat.ConvertTo(accumulator, MatType.CV_64F, weight);
            for (var index = 1; index < images.Count; index++)
            {
                using var weighted = new Mat();
                images[index].Mat.ConvertTo(weighted, MatType.CV_64F, weight);
                Cv2.Add(accumulator, weighted, accumulator);
            }
            return accumulator.Clone();
        }

        images[0].Mat.ConvertTo(accumulator, MatType.CV_32F);
        for (var index = 1; index < images.Count; index++)
        {
            using var current = new Mat();
            images[index].Mat.ConvertTo(current, MatType.CV_32F);
            Cv2.Add(accumulator, current, accumulator);
        }
        Cv2.Multiply(accumulator, 1.0 / images.Count, accumulator);
        return accumulator.Clone();
    }

    private static Mat Fold(List<VisionImage> images, Action<Mat, Mat, Mat> combine)
    {
        var output = images[0].Mat.Clone();
        try
        {
            for (var index = 1; index < images.Count; index++)
            {
                var next = new Mat();
                try { combine(output, images[index].Mat, next); }
                catch { next.Dispose(); throw; }
                output.Dispose();
                output = next;
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Mat Median(List<VisionImage> images, CancellationToken cancellationToken)
    {
        var reference = images[0].Mat;
        if (reference.Depth() != MatType.CV_8U)
            throw new InvalidOperationException(
                $"image.fuse median supports 8-bit images only; received {reference.Depth()}. Use mean/min/max for 16-bit data.");

        var rows = reference.Rows;
        var cols = reference.Cols;
        var channels = reference.Channels();
        var planes = new List<Mat[]>(images.Count);
        var outputPlanes = new Mat[channels];
        var buffer = new byte[images.Count];
        try
        {
            foreach (var image in images) planes.Add(Cv2.Split(image.Mat));
            for (var channel = 0; channel < channels; channel++)
            {
                outputPlanes[channel] = new Mat(rows, cols, MatType.CV_8UC1);
                for (var y = 0; y < rows; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < cols; x++)
                    {
                        for (var index = 0; index < images.Count; index++)
                            buffer[index] = planes[index][channel].At<byte>(y, x);
                        SortBytes(buffer);
                        var median = images.Count % 2 == 1
                            ? buffer[images.Count / 2]
                            : (buffer[images.Count / 2 - 1] + buffer[images.Count / 2]) / 2;
                        outputPlanes[channel].Set(y, x, (byte)median);
                    }
                }
            }

            var output = new Mat();
            try { Cv2.Merge(outputPlanes, output); return output; }
            catch { output.Dispose(); throw; }
        }
        finally
        {
            foreach (var plane in planes)
                foreach (var channel in plane) channel.Dispose();
            foreach (var plane in outputPlanes) plane?.Dispose();
        }
    }

    private static void SortBytes(byte[] values)
    {
        for (var index = 1; index < values.Length; index++)
        {
            var current = values[index];
            var cursor = index - 1;
            while (cursor >= 0 && values[cursor] > current)
            {
                values[cursor + 1] = values[cursor];
                cursor--;
            }
            values[cursor + 1] = current;
        }
    }
}
