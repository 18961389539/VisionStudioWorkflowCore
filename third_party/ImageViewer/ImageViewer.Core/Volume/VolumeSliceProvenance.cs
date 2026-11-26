#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Linq;

namespace ImageViewer.Models
{
    /// <summary>
    /// Records the metadata used to place one slice in a volume.
    /// </summary>
    public sealed record VolumeSliceProvenance(
        string Path,
        double? Position = null,
        int? InstanceNumber = null,
        DateTimeOffset? AcquisitionTime = null);

    /// <summary>
    /// Auditable ordering information retained with a loaded volume.
    /// </summary>
    public sealed record VolumeSliceOrderMetadata(
        string Source,
        bool IsAuthoritative,
        IReadOnlyList<VolumeSliceProvenance> Slices)
    {
        public VolumeSliceOrderMetadata(string source, bool isAuthoritative, IEnumerable<VolumeSliceProvenance> slices)
            : this(source, isAuthoritative, Materialize(slices))
        {
        }

        private static IReadOnlyList<VolumeSliceProvenance> Materialize(IEnumerable<VolumeSliceProvenance> slices)
        {
            ArgumentNullException.ThrowIfNull(slices);
            VolumeSliceProvenance[] materialized = slices.ToArray();
            if (materialized.Length == 0)
            {
                throw new ArgumentException("At least one slice provenance item is required.", nameof(slices));
            }

            if (materialized.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path)))
            {
                throw new ArgumentException("Slice provenance paths must not be empty.", nameof(slices));
            }

            if (materialized.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
            {
                throw new ArgumentException("Slice provenance paths must be unique.", nameof(slices));
            }

            return Array.AsReadOnly(materialized);
        }
    }
}
#pragma warning restore CS1591
