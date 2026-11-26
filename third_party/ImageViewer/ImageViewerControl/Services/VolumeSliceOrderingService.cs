using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Localization;

namespace ImageViewer.Services
{
    public enum VolumeSliceOrderSource
    {
        ExplicitPosition,
        InstanceNumber,
        AcquisitionTime,
        NaturalFileName
    }

    /// <summary>
    /// 一张体数据切片及其可用于排序的采集元数据。
    /// </summary>
    public sealed record VolumeSliceDescriptor(
        string Path,
        double? Position = null,
        int? InstanceNumber = null,
        DateTimeOffset? AcquisitionTime = null,
        double? SpacingX = null,
        double? SpacingY = null,
        double? SpacingZ = null);

    public sealed record VolumeSliceOrderingResult(
        IReadOnlyList<VolumeSliceDescriptor> Slices,
        VolumeSliceOrderSource Source,
        bool IsAuthoritative,
        IReadOnlyList<string> Warnings)
    {
        public IReadOnlyList<string> OrderedPaths => Slices.Select(slice => slice.Path).ToArray();
    }

    /// <summary>Spacing derived from adjacent positions in the ordered stack.</summary>
    public sealed record VolumeSlicePositionSpacing(
        double? Interval,
        bool HasIrregularIntervals,
        bool ConflictsWithDeclaredSpacing);

    /// <summary>
    /// 为多文件体数据提供元数据优先的确定性排序。
    /// Chinese: 优先使用空间位置、实例号或采集时间；只有元数据不完整时才回退到文件名，
    /// 并返回警告让宿主要求人工确认。
    /// English: Prefers physical position, instance number, or acquisition time and only falls back
    /// to filename order when metadata is incomplete, returning a warning for operator confirmation.
    /// </summary>
    public static class VolumeSliceOrderingService
    {
        private const double RelativeSpacingTolerance = 0.001;
        private const int MaxConcurrentSidecarReads = 8;

        /// <summary>
        /// Estimates the nominal Z spacing from the median positive gap. Irregular or duplicate
        /// positions cannot be represented exactly by a uniformly spaced volume.
        /// </summary>
        public static VolumeSlicePositionSpacing AnalyzePositionSpacing(VolumeSliceOrderingResult ordering)
        {
            ArgumentNullException.ThrowIfNull(ordering);
            IReadOnlyList<VolumeSliceDescriptor> slices = ordering.Slices;
            if (ordering.Source != VolumeSliceOrderSource.ExplicitPosition || slices.Count < 2)
            {
                return new VolumeSlicePositionSpacing(null, false, false);
            }

            var gaps = new List<double>(slices.Count - 1);
            for (int index = 1; index < slices.Count; index++)
            {
                double gap = (slices[index].Position ?? double.NaN) - (slices[index - 1].Position ?? double.NaN);
                if (double.IsFinite(gap) && gap > 0)
                {
                    gaps.Add(gap);
                }
            }

            if (gaps.Count == 0)
            {
                return new VolumeSlicePositionSpacing(null, true, false);
            }

            gaps.Sort();
            int middle = gaps.Count / 2;
            double median = gaps[middle];
            if (gaps.Count % 2 == 0)
            {
                median = gaps[middle - 1] + (median - gaps[middle - 1]) / 2;
            }
            bool irregular = gaps.Count != slices.Count - 1 || gaps.Any(gap => !SpacingAgrees(gap, median));
            bool conflicts = slices.Any(slice => slice.SpacingZ is { } declared && double.IsFinite(declared) &&
                declared > 0 && !SpacingAgrees(declared, median));
            return new VolumeSlicePositionSpacing(median, irregular, conflicts);
        }

        private static bool SpacingAgrees(double value, double reference) =>
            Math.Abs(value / reference - 1) <= RelativeSpacingTolerance;

        public static VolumeSliceOrderingResult OrderDescriptors(IEnumerable<VolumeSliceDescriptor> descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors);

            VolumeSliceDescriptor[] materialized = descriptors.ToArray();
            ValidatePaths(materialized.Select(descriptor => descriptor.Path));
            if (materialized.Length == 0)
            {
                throw new ArgumentException("At least one slice path is required.", nameof(descriptors));
            }

            if (materialized.All(descriptor => descriptor.Position is { } position && double.IsFinite(position)))
            {
                return CreateResult(
                    materialized
                        .OrderBy(descriptor => descriptor.Position!.Value)
                        .ThenBy(descriptor => descriptor.InstanceNumber)
                        .ThenBy(descriptor => Path.GetFileName(descriptor.Path), NaturalFileNameComparer.Instance)
                        .ThenBy(descriptor => descriptor.Path, StringComparer.OrdinalIgnoreCase),
                    VolumeSliceOrderSource.ExplicitPosition,
                    warnings: materialized.GroupBy(descriptor => descriptor.Position!.Value).Any(group => group.Count() > 1)
                        ? ["多个切片具有相同空间位置，已使用实例号和文件名作为并列排序依据。"]
                        : []);
            }

            if (materialized.All(descriptor => descriptor.InstanceNumber.HasValue))
            {
                return CreateResult(
                    materialized
                        .OrderBy(descriptor => descriptor.InstanceNumber!.Value)
                        .ThenBy(descriptor => descriptor.Position)
                        .ThenBy(descriptor => Path.GetFileName(descriptor.Path), NaturalFileNameComparer.Instance)
                        .ThenBy(descriptor => descriptor.Path, StringComparer.OrdinalIgnoreCase),
                    VolumeSliceOrderSource.InstanceNumber,
                    warnings: materialized.GroupBy(descriptor => descriptor.InstanceNumber!.Value).Any(group => group.Count() > 1)
                        ? ["多个切片具有相同实例号，已使用空间位置和文件名作为并列排序依据。"]
                        : []);
            }

            if (materialized.All(descriptor => descriptor.AcquisitionTime.HasValue))
            {
                return CreateResult(
                    materialized
                        .OrderBy(descriptor => descriptor.AcquisitionTime!.Value)
                        .ThenBy(descriptor => descriptor.InstanceNumber)
                        .ThenBy(descriptor => Path.GetFileName(descriptor.Path), NaturalFileNameComparer.Instance)
                        .ThenBy(descriptor => descriptor.Path, StringComparer.OrdinalIgnoreCase),
                    VolumeSliceOrderSource.AcquisitionTime,
                    warnings: []);
            }

            return CreateResult(
                materialized
                    .OrderBy(descriptor => Path.GetFileName(descriptor.Path), NaturalFileNameComparer.Instance)
                    .ThenBy(descriptor => descriptor.Path, StringComparer.OrdinalIgnoreCase),
                VolumeSliceOrderSource.NaturalFileName,
                ["未找到完整的切片空间位置、实例号或采集时间元数据，已回退到文件名自然排序；请确认切片顺序。"]);
        }

        public static IReadOnlyList<string> OrderPaths(IEnumerable<string> paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            return OrderDescriptors(paths.Select(path => new VolumeSliceDescriptor(path))).OrderedPaths;
        }

        /// <summary>
        /// 从相邻的 .slice.json 或同名 .json 侧车文件读取排序元数据。
        /// 侧车文件支持 zPosition/position、instanceNumber、acquisitionTime 字段。
        /// </summary>
        public static async Task<VolumeSliceOrderingResult> OrderPathsWithMetadataAsync(
            IEnumerable<string> paths,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            string[] materialized = paths.ToArray();
            ValidatePaths(materialized);
            if (materialized.Length == 0)
            {
                throw new ArgumentException("At least one slice path is required.", nameof(paths));
            }

            var metadata = new SliceMetadata[materialized.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, materialized.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentSidecarReads,
                CancellationToken = cancellationToken
            }, async (index, token) =>
            {
                metadata[index] = await ReadDescriptorAsync(materialized[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            VolumeSliceOrderingResult result = OrderDescriptors(metadata.Select(item => item.Descriptor));
            var warnings = new List<string>(result.Warnings);
            if (result.Source != VolumeSliceOrderSource.ExplicitPosition)
            {
                AddPartialFieldWarning(result.Slices, slice => slice.Position.HasValue, "VolumeMetadataPosition", warnings);
            }
            if (result.Source is VolumeSliceOrderSource.AcquisitionTime or VolumeSliceOrderSource.NaturalFileName)
            {
                AddPartialFieldWarning(result.Slices, slice => slice.InstanceNumber.HasValue, "VolumeMetadataInstance", warnings);
            }
            if (result.Source == VolumeSliceOrderSource.NaturalFileName)
            {
                AddPartialFieldWarning(result.Slices, slice => slice.AcquisitionTime.HasValue, "VolumeMetadataAcquisitionTime", warnings);
            }
            warnings.AddRange(metadata.SelectMany(item => item.Warnings));
            return result with { Warnings = warnings.AsReadOnly() };
        }

        private static void AddPartialFieldWarning(IReadOnlyList<VolumeSliceDescriptor> slices,
            Func<VolumeSliceDescriptor, bool> hasValue, string fieldKey, List<string> warnings)
        {
            string[] missing = slices.Where(slice => !hasValue(slice)).Select(slice => slice.Path).ToArray();
            if (missing.Length == 0 || missing.Length == slices.Count)
            {
                return;
            }

            warnings.AddRange(missing.Select(path =>
                UiText.Format("VolumeMetadataFieldMissing", Path.GetFileName(path), UiText.Get(fieldKey))));
        }

        public static bool HasOrderChanged(IReadOnlyList<string> original, IReadOnlyList<string> ordered)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(ordered);

            if (original.Count != ordered.Count)
            {
                return true;
            }

            for (int index = 0; index < original.Count; index++)
            {
                if (!string.Equals(original[index], ordered[index], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static VolumeSliceOrderingResult CreateResult(
            IEnumerable<VolumeSliceDescriptor> ordered,
            VolumeSliceOrderSource source,
            IReadOnlyList<string> warnings)
        {
            return new VolumeSliceOrderingResult(
                ordered.ToArray(),
                source,
                source != VolumeSliceOrderSource.NaturalFileName,
                warnings);
        }

        private static void ValidatePaths(IEnumerable<string> paths)
        {
            string[] materialized = paths.ToArray();
            if (materialized.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Slice paths must not be null or whitespace.", nameof(paths));
            }

            if (materialized.Length != materialized.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                throw new ArgumentException("Slice paths must be unique.", nameof(paths));
            }
        }

        private static async Task<SliceMetadata> ReadDescriptorAsync(string path, CancellationToken cancellationToken)
        {
            var warnings = new List<string>();
            foreach (string sidecarPath in GetSidecarPaths(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool earlierSidecarWasRejected = warnings.Count > 0;
                try
                {
                    await using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add(UiText.Format("VolumeMetadataInvalidSidecar", sidecarPath));
                        continue;
                    }

                    var invalidFields = new List<string>();
                    var descriptor = new VolumeSliceDescriptor(
                        path,
                        ReadField(root, ParseDouble, invalidFields, "zPosition", "position", "z"),
                        ReadField(root, ParseInt, invalidFields, "instanceNumber", "instance"),
                        ReadField(root, ParseDateTime, invalidFields, "acquisitionTime", "acquiredAt"),
                        ReadField(root, ParsePositiveDouble, invalidFields, "spacingX", "pixelSpacingX"),
                        ReadField(root, ParsePositiveDouble, invalidFields, "spacingY", "pixelSpacingY"),
                        ReadField(root, ParsePositiveDouble, invalidFields, "spacingZ", "sliceSpacing", "pixelSpacingZ"));
                    if (invalidFields.Count > 0)
                    {
                        warnings.Add(UiText.Format("VolumeMetadataInvalidFields", sidecarPath, string.Join(", ", invalidFields)));
                    }
                    if (descriptor.Position.HasValue || descriptor.InstanceNumber.HasValue || descriptor.AcquisitionTime.HasValue ||
                        descriptor.SpacingX.HasValue || descriptor.SpacingY.HasValue || descriptor.SpacingZ.HasValue)
                    {
                        if (earlierSidecarWasRejected)
                        {
                            warnings.Add(UiText.Format("VolumeMetadataFallbackSidecar", sidecarPath));
                        }
                        return new SliceMetadata(descriptor, warnings);
                    }

                    if (invalidFields.Count == 0)
                    {
                        warnings.Add(UiText.Format("VolumeMetadataNoUsableFields", sidecarPath));
                    }
                }
                catch (JsonException)
                {
                    warnings.Add(UiText.Format("VolumeMetadataInvalidSidecar", sidecarPath));
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Sidecars are optional.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(UiText.Format("VolumeMetadataUnreadableSidecar", sidecarPath));
                }
            }

            return new SliceMetadata(new VolumeSliceDescriptor(path), warnings);
        }

        private sealed record SliceMetadata(VolumeSliceDescriptor Descriptor, IReadOnlyList<string> Warnings);

        private static IEnumerable<string> GetSidecarPaths(string path)
        {
            yield return path + ".slice.json";
            string legacy = Path.ChangeExtension(path, ".json");
            if (!string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase))
            {
                yield return legacy;
            }
        }

        private static T? ReadField<T>(JsonElement root, Func<JsonElement, T?> parse,
            List<string> invalidFields, params string[] aliases) where T : struct
        {
            foreach (string name in aliases)
            {
                if (root.TryGetProperty(name, out JsonElement value))
                {
                    if (parse(value) is { } parsed)
                    {
                        return parsed;
                    }
                    invalidFields.Add(name);
                }
            }
            return null;
        }

        private static double? ParseDouble(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)) return number;
            return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) ? number : null;
        }

        private static double? ParsePositiveDouble(JsonElement element)
        {
            double? value = ParseDouble(element);
            return value is { } number && number > 0 ? number : null;
        }

        private static int? ParseInt(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
            return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
        }

        private static DateTimeOffset? ParseDateTime(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
                ? timestamp
                : null;
        }

        private sealed class NaturalFileNameComparer : IComparer<string?>
        {
            public static NaturalFileNameComparer Instance { get; } = new();

            public int Compare(string? x, string? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;

                int xIndex = 0;
                int yIndex = 0;
                while (xIndex < x.Length && yIndex < y.Length)
                {
                    char xChar = x[xIndex];
                    char yChar = y[yIndex];
                    bool xIsDigit = char.IsDigit(xChar);
                    bool yIsDigit = char.IsDigit(yChar);

                    if (xIsDigit && yIsDigit)
                    {
                        int xEnd = ReadDigitRunEnd(x, xIndex);
                        int yEnd = ReadDigitRunEnd(y, yIndex);
                        int xSignificantStart = SkipLeadingZeros(x, xIndex, xEnd);
                        int ySignificantStart = SkipLeadingZeros(y, yIndex, yEnd);
                        int xSignificantLength = xEnd - xSignificantStart;
                        int ySignificantLength = yEnd - ySignificantStart;

                        int result = xSignificantLength.CompareTo(ySignificantLength);
                        if (result != 0) return result;

                        result = string.CompareOrdinal(
                            x,
                            xSignificantStart,
                            y,
                            ySignificantStart,
                            xSignificantLength);
                        if (result != 0) return result;

                        // 数值相同（例如 2 与 002）时，让较短的写法先出现，结果仍然稳定。
                        result = (xEnd - xIndex).CompareTo(yEnd - yIndex);
                        if (result != 0) return result;

                        xIndex = xEnd;
                        yIndex = yEnd;
                        continue;
                    }

                    int characterResult = char.ToUpperInvariant(xChar).CompareTo(char.ToUpperInvariant(yChar));
                    if (characterResult != 0) return characterResult;

                    xIndex++;
                    yIndex++;
                }

                return x.Length.CompareTo(y.Length);
            }

            private static int ReadDigitRunEnd(string value, int start)
            {
                int index = start;
                while (index < value.Length && char.IsDigit(value[index]))
                {
                    index++;
                }

                return index;
            }

            private static int SkipLeadingZeros(string value, int start, int end)
            {
                int index = start;
                while (index < end - 1 && value[index] == '0')
                {
                    index++;
                }

                return index;
            }
        }
    }
}
