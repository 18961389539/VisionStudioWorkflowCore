using System;
using System.IO;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Unit")]
    public class ImageViewerExceptionClassifierTests
    {
        [Theory]
        [InlineData(typeof(UnauthorizedAccessException), "Permission")]
        [InlineData(typeof(IOException), "IO")]
        [InlineData(typeof(InvalidDataException), "Data")]
        [InlineData(typeof(FormatException), "Format")]
        [InlineData(typeof(NotSupportedException), "Unsupported")]
        [InlineData(typeof(InvalidOperationException), "Unexpected")]
        public void Classify_ReturnsStableCategory(Type exceptionType, string expectedCategory)
        {
            Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;

            Assert.Equal(expectedCategory, ImageViewerExceptionClassifier.Classify(exception));
        }
    }
}