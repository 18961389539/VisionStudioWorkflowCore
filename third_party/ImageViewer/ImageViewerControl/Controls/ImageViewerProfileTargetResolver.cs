using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal interface IImageViewerProfileTargetResolver
    {
        LineMeasureRoi? GetProfileTargetLine();
    }

    internal sealed class ImageViewerProfileTargetResolver : IImageViewerProfileTargetResolver
    {
        private readonly Func<LineMeasureRoi?> _currentLineMeasureProvider;
        private readonly Func<RoiBase?> _selectedRoiProvider;

        public ImageViewerProfileTargetResolver(
            Func<LineMeasureRoi?> currentLineMeasureProvider,
            Func<RoiBase?> selectedRoiProvider)
        {
            _currentLineMeasureProvider = currentLineMeasureProvider ?? throw new ArgumentNullException(nameof(currentLineMeasureProvider));
            _selectedRoiProvider = selectedRoiProvider ?? throw new ArgumentNullException(nameof(selectedRoiProvider));
        }

        public LineMeasureRoi? GetProfileTargetLine()
        {
            LineMeasureRoi? currentLineMeasure = _currentLineMeasureProvider();
            if (currentLineMeasure is not ArrowAnnotationRoi && currentLineMeasure != null)
            {
                return currentLineMeasure;
            }

            RoiBase? selectedRoi = _selectedRoiProvider();
            return selectedRoi is LineMeasureRoi line && selectedRoi is not ArrowAnnotationRoi
                ? line
                : null;
        }
    }
}
