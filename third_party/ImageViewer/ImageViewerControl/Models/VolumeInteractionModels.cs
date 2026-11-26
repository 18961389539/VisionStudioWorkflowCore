using System;

namespace ImageViewer.Models
{
    public sealed record VolumeCropBounds(
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY,
        int MinimumZ,
        int MaximumZ)
    {
        public static VolumeCropBounds Full(VolumeData volume) => new(0, volume.Width - 1, 0, volume.Height - 1, 0, volume.Depth - 1);

        public VolumeCropBounds Clamp(VolumeData volume)
        {
            ArgumentNullException.ThrowIfNull(volume);
            VolumeCropBounds clamped = new(
                Math.Clamp(MinimumX, 0, volume.Width - 1),
                Math.Clamp(MaximumX, 0, volume.Width - 1),
                Math.Clamp(MinimumY, 0, volume.Height - 1),
                Math.Clamp(MaximumY, 0, volume.Height - 1),
                Math.Clamp(MinimumZ, 0, volume.Depth - 1),
                Math.Clamp(MaximumZ, 0, volume.Depth - 1));
            return new VolumeCropBounds(
                Math.Min(clamped.MinimumX, clamped.MaximumX),
                Math.Max(clamped.MinimumX, clamped.MaximumX),
                Math.Min(clamped.MinimumY, clamped.MaximumY),
                Math.Max(clamped.MinimumY, clamped.MaximumY),
                Math.Min(clamped.MinimumZ, clamped.MaximumZ),
                Math.Max(clamped.MinimumZ, clamped.MaximumZ));
        }

        public bool IsValid => MinimumX <= MaximumX && MinimumY <= MaximumY && MinimumZ <= MaximumZ;
    }

    public sealed record VolumeVoxelLocation(
        int X,
        int Y,
        int Z,
        double PhysicalX,
        double PhysicalY,
        double PhysicalZ,
        ushort Intensity);

    public sealed class VolumeVoxelPickedEventArgs : EventArgs
    {
        public VolumeVoxelPickedEventArgs(VolumeVoxelLocation voxel)
        {
            Voxel = voxel;
        }

        public VolumeVoxelLocation Voxel { get; }
    }
}
