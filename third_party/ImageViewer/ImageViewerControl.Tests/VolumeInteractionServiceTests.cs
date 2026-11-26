using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class VolumeInteractionServiceTests
    {
        [Fact]
        public void NormalizeCrop_DefaultsToFullVolume()
        {
            var volume = CreateVolume();

            VolumeCropBounds bounds = VolumeInteractionService.NormalizeCrop(volume, null);

            Assert.Equal(new VolumeCropBounds(0, 1, 0, 2, 0, 1), bounds);
        }

        [Fact]
        public void NormalizeCrop_ClampsCoordinatesToVolume()
        {
            var volume = CreateVolume();

            VolumeCropBounds bounds = VolumeInteractionService.NormalizeCrop(
                volume,
                new VolumeCropBounds(-5, 9, -3, 8, -2, 7));

            Assert.Equal(new VolumeCropBounds(0, 1, 0, 2, 0, 1), bounds);
        }

        [Fact]
        public void ReadVoxel_ReturnsIntensityAndPhysicalCoordinates()
        {
            var volume = new VolumeData(
                [CreateBitmap(10, 20, 30, 40, 50, 60), CreateBitmap(70, 80, 90, 100, 110, 120)],
                spacingX: 0.5,
                spacingY: 0.75,
                spacingZ: 1.25);

            VolumeVoxelLocation voxel = VolumeInteractionService.ReadVoxel(volume, 1, 2, 1);

            Assert.Equal(120, voxel.Intensity);
            Assert.Equal(0.5, voxel.PhysicalX);
            Assert.Equal(1.5, voxel.PhysicalY);
            Assert.Equal(1.25, voxel.PhysicalZ);
        }

        [Fact]
        public void NormalizeCrop_SortsInvertedRanges()
        {
            var volume = CreateVolume();

            VolumeCropBounds bounds = VolumeInteractionService.NormalizeCrop(
                volume,
                new VolumeCropBounds(1, 0, 2, 0, 1, 0));

            Assert.Equal(new VolumeCropBounds(0, 1, 0, 2, 0, 1), bounds);
            Assert.True(bounds.IsValid);
        }

        [Fact]
        public void TryReadVoxelAtWorldPoint_UsesCenteredVolumeCoordinates()
        {
            var volume = new VolumeData(
                [CreateBitmap(10, 20, 30, 40, 50, 60), CreateBitmap(70, 80, 90, 100, 110, 120)],
                spacingX: 0.5,
                spacingY: 0.75,
                spacingZ: 1.25);

            bool found = VolumeInteractionService.TryReadVoxelAtWorldPoint(
                volume,
                new Point3D(0.25, 0.9375, 0.625),
                out VolumeVoxelLocation? voxel);

            Assert.True(found);
            Assert.NotNull(voxel);
            Assert.Equal(1, voxel!.X);
            Assert.Equal(2, voxel.Y);
            Assert.Equal(1, voxel.Z);
            Assert.Equal(120, voxel.Intensity);
        }

        [Fact]
        public void TryReadVoxelAtWorldPoint_ReturnsFalseOutsideVolume()
        {
            Assert.False(VolumeInteractionService.TryReadVoxelAtWorldPoint(CreateVolume(), new Point3D(2, 0, 0), out _));
        }

        private static VolumeData CreateVolume() => new([CreateBitmap(1, 2, 3, 4, 5, 6), CreateBitmap(7, 8, 9, 10, 11, 12)]);

        private static BitmapSource CreateBitmap(params byte[] values)
        {
            return BitmapSource.Create(2, 3, 96, 96, PixelFormats.Gray8, null, values, 2);
        }
    }
}
