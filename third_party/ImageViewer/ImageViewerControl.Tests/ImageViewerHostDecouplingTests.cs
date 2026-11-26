using System;
using System.IO;
using System.Linq;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerHostDecouplingTests
    {
        [Fact]
        public void ImageViewer_NoLongerImplementsWorkflowCommandAndPersistenceHostInterfaces()
        {
            Type[] interfaces = typeof(ImageViewer.Controls.ImageViewer).GetInterfaces();

            Assert.DoesNotContain(typeof(IImageViewerFeatureMenuCommandHost), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerViewOptions), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerViewportOperations), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerImageTransformOperations), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerModeCommandHost), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerAnalysisCommandHost), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerRoiMenuCommandHost), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerFileMenuCommandHost), interfaces);
            Assert.DoesNotContain(typeof(IImageViewerRoiPersistenceControllerHost), interfaces);
        }

        [Fact]
        public void CommandComposition_UsesCapabilitiesInsteadOfCommandDelegateBags()
        {
            string projectDirectory = FindProjectDirectory();
            string assembler = File.ReadAllText(Path.Combine(projectDirectory, "Controls", "ImageViewer.CommandControllerAssembler.cs"));

            Assert.DoesNotContain("CommandDependencies", assembler, StringComparison.Ordinal);
            Assert.Contains("ImageViewerFeatureAnalysisCapability", assembler, StringComparison.Ordinal);
            Assert.Contains("ImageViewerRoiSelectionCapability", assembler, StringComparison.Ordinal);
            Assert.Contains("ImageViewerViewOptionsAdapter", assembler, StringComparison.Ordinal);
        }

        private static string FindProjectDirectory()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "ImageViewerControl", "ImageViewerControl.csproj");
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(candidate)!;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("ImageViewerControl.csproj");
        }
    }
}
