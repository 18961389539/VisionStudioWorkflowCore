using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class VolumeDataTests
    {
        [Fact]
        public void Constructor_StoresDimensionsSpacingAndSlices()
        {
            BitmapSource first = CreateBitmap(2, 3, 10);
            BitmapSource second = CreateBitmap(2, 3, 20);
            var volume = new VolumeData([first, second], 0.5, 0.75, 1.25);

            Assert.Equal(2, volume.Width);
            Assert.Equal(3, volume.Height);
            Assert.Equal(2, volume.Depth);
            Assert.Equal(0.5, volume.SpacingX);
            Assert.Equal(0.75, volume.SpacingY);
            Assert.Equal(1.25, volume.SpacingZ);
            Assert.Same(volume.Slices[0], volume.GetAxialSlice(0));
            Assert.Same(volume.Slices[1], volume.GetAxialSlice(1));
        }

        [Fact]
        public void Constructor_RejectsEmptyOrInconsistentSlices()
        {
            Assert.Throws<ArgumentException>(() => new VolumeData([]));
            Assert.Throws<ArgumentException>(() => new VolumeData([CreateBitmap(2, 2, 1), CreateBitmap(3, 2, 2)]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeData([CreateBitmap(2, 2, 1)], spacingZ: 0));
        }

        [Fact]
        public void Constructor_RetainsSliceOrderProvenance()
        {
            var metadata = new VolumeSliceOrderMetadata(
                "ExplicitPosition",
                true,
                [
                    new VolumeSliceProvenance("slice-a", -1.5),
                    new VolumeSliceProvenance("slice-b", 2.5)
                ]);

            var volume = new VolumeData([CreateBitmap(2, 2, 1), CreateBitmap(2, 2, 2)], orderMetadata: metadata);

            Assert.Same(metadata, volume.SliceOrderMetadata);
            Assert.Equal("slice-a", volume.SliceOrderMetadata!.Slices[0].Path);
            Assert.Equal(-1.5, volume.SliceOrderMetadata.Slices[0].Position);
        }

        [Fact]
        public void Constructor_RejectsProvenanceDepthMismatch()
        {
            var metadata = new VolumeSliceOrderMetadata(
                "NaturalFileName",
                false,
                [new VolumeSliceProvenance("only-slice")]);

            Assert.Throws<ArgumentException>(() => new VolumeData(
                [CreateBitmap(2, 2, 1), CreateBitmap(2, 2, 2)],
                orderMetadata: metadata));
        }

        [Fact]
        public void GetAxialSlice_RejectsInvalidIndex()
        {
            var volume = new VolumeData([CreateBitmap(2, 2, 1)]);

            Assert.Throws<ArgumentOutOfRangeException>(() => volume.GetAxialSlice(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => volume.GetAxialSlice(1));
        }

        [Fact]
        public void SliceService_SupportsAxialOrientation()
        {
            BitmapSource slice = CreateBitmap(2, 2, 42);
            var volume = new VolumeData([slice]);

            BitmapSource result = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Axial, 0);

            Assert.Equal(slice.PixelWidth, result.PixelWidth);
            Assert.Equal((byte)42, ReadFirstPixel(result));
        }

        private static BitmapSource CreateBitmap(int width, int height, byte value)
        {
            byte[] pixels = new byte[width * height];
            Array.Fill(pixels, value);
            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Gray8,
                null,
                pixels,
                width);
        }

            private static byte ReadFirstPixel(BitmapSource bitmap)
            {
                byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth, 0);
                return pixels[0];
            }
    }
}
