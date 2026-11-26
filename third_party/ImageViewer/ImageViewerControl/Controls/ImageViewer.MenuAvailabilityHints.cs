using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;
using ImageViewer.Services;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private readonly ConditionalWeakTable<MenuItem, OriginalMenuToolTip> _originalMenuToolTips = new();

    private void ApplyMenuAvailabilityHints()
    {
        bool hasBitmap = GetAnalysisBitmapSource() != null;
        Visit(mainContextMenu);

        void Visit(ItemsControl parent)
        {
            foreach (MenuItem item in parent.Items.OfType<MenuItem>())
            {
                ToolTipService.SetShowOnDisabled(item, true);
                if (item.Tag is ImageViewerDetectionRecommendationMenuTag)
                {
                    string? reason = GetDetectionRecommendationUnavailableReason();
                    item.SetCurrentValue(IsEnabledProperty, reason == null);
                    item.SetCurrentValue(ToolTipProperty, UiText.Get(reason ?? "DetectionRecommendationHint"));
                }
                else if (item.Tag is ImageViewerAdvancedMeasurementMenuTag advanced)
                {
                    var availability = AdvancedMeasurementMenuCatalog.GetAvailability(advanced.Kind, ViewerState.AllRois, hasBitmap, ViewerState.SelectedRoi);
                    if (advanced.InitialSource is { } source && (!ViewerState.AllRois.Contains(source)
                        || !AdvancedMeasurementMenuCatalog.CanSeed(advanced.Kind, source, hasBitmap)))
                        availability = new(false, "MenuNeedCompatibleSource");
                    item.SetCurrentValue(IsEnabledProperty, availability.IsEnabled);
                    item.SetCurrentValue(ToolTipProperty, availability.ReasonKey is { } reason
                        ? UiText.Get(reason) : advanced.HintKey != null ? UiText.Get(advanced.HintKey)
                            : UiText.Format("MenuMeasurementLaunchHint", UiText.Get(advanced.Descriptor.HeaderKey)));
                }
                else if (item.Tag is ImageViewerRoiToolMenuTag)
                {
                    item.SetCurrentValue(IsEnabledProperty, hasBitmap);
                    item.SetCurrentValue(ToolTipProperty, hasBitmap ? item.Header?.ToString() : UiText.Get("MenuNeedImage"));
                }
                else if (item.Tag is not (MenuSearchResultTag or MenuSearchHostTag))
                {
                    var original = _originalMenuToolTips.GetValue(item, static menu => new OriginalMenuToolTip(menu.ToolTip));
                    if (!item.IsEnabled)
                    {
                        string reason = ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.Undo) ? "MenuNothingToUndo"
                            : ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.Redo) ? "MenuNothingToRedo"
                            : ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.ClearAll) ? "MenuNoRois"
                            : ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.CalibratePixels) ? "MenuNeedCalibrationLine"
                            : ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.EditCaliperSettings) ? "MenuNeedCaliperSelection"
                            : ReferenceEquals(item.Tag, ImageViewerFeatureMenuTags.GradientDetect)
                                ? "MenuNeedDetectableCaliper"
                            : !hasBitmap ? "MenuNeedImage" : "MenuUnavailable";
                        item.SetCurrentValue(ToolTipProperty, original.Value == null ? UiText.Get(reason)
                            : original.Value + "\n" + UiText.Get(reason));
                    }
                    else item.SetCurrentValue(ToolTipProperty, original.Value);
                }
                Visit(item);
            }
        }
    }

    private sealed record OriginalMenuToolTip(object? Value);
}
