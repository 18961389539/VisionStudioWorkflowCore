using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ImageViewer.Models
{
    public sealed class VolumeData
    {
        public VolumeData(
            IEnumerable<BitmapSource> axialSlices,
            double spacingX = 1.0,
            double spacingY = 1.0,
            double spacingZ = 1.0,
            VolumeSliceOrderMetadata? orderMetadata = null)
        {
            ArgumentNullException.ThrowIfNull(axialSlices);
            ValidateSpacing(spacingX, nameof(spacingX));
            ValidateSpacing(spacingY, nameof(spacingY));
            ValidateSpacing(spacingZ, nameof(spacingZ));

            BitmapSource[] frozenSlices = axialSlices
                .Select(EnsureFrozen)
                .ToArray();
            if (frozenSlices.Length == 0)
            {
                throw new ArgumentException("At least one axial slice is required.", nameof(axialSlices));
            }

            int width = frozenSlices[0].PixelWidth;
            int height = frozenSlices[0].PixelHeight;
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Axial slices must have positive dimensions.", nameof(axialSlices));
            }

            if (frozenSlices.Any(slice => slice.PixelWidth != width || slice.PixelHeight != height))
            {
                throw new ArgumentException("All axial slices must have identical dimensions.", nameof(axialSlices));
            }

            if (orderMetadata is not null && orderMetadata.Slices.Count != frozenSlices.Length)
            {
                throw new ArgumentException("Slice order metadata must contain one item per axial slice.", nameof(orderMetadata));
            }

            Slices = new ReadOnlyCollection<BitmapSource>(frozenSlices);
            Width = width;
            Height = height;
            Depth = frozenSlices.Length;
            SpacingX = spacingX;
            SpacingY = spacingY;
            SpacingZ = spacingZ;
            SliceOrderMetadata = orderMetadata;
        }

        public IReadOnlyList<BitmapSource> Slices { get; }

        public int Width { get; }

        public int Height { get; }

        public int Depth { get; }

        public double SpacingX { get; }

        public double SpacingY { get; }

        public double SpacingZ { get; }

        /// <summary>
        /// The exact ordering evidence used when this volume was assembled.
        /// </summary>
        public VolumeSliceOrderMetadata? SliceOrderMetadata { get; }

        public BitmapSource GetAxialSlice(int sliceIndex)
        {
            if ((uint)sliceIndex >= (uint)Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(sliceIndex), sliceIndex, $"Slice index must be between 0 and {Depth - 1}.");
            }

            return Slices[sliceIndex];
        }

        private static BitmapSource EnsureFrozen(BitmapSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.IsFrozen)
            {
                return source;
            }

            BitmapSource clone = source.Clone();
            if (clone.CanFreeze)
            {
                clone.Freeze();
            }

            return clone;
        }

        private static void ValidateSpacing(double spacing, string parameterName)
        {
            if (double.IsNaN(spacing) || double.IsInfinity(spacing) || spacing <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, spacing, "Voxel spacing must be a finite positive value.");
            }
        }
    }
}
