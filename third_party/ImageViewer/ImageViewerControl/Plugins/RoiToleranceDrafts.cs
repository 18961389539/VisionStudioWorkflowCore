using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Plugins
{
    // Invalid editor text belongs to the live ROI, not to its saved measurement.
    // Keep it across editor rebuilds and release it when the ROI is no longer referenced.
    internal static class RoiToleranceDrafts
    {
        private static readonly ConditionalWeakTable<RoiBase, Dictionary<string, string>> InvalidInputs = new();

        public static string? GetText(RoiBase roi, string field) =>
            InvalidInputs.TryGetValue(roi, out var inputs) ? inputs.GetValueOrDefault(field) : null;

        public static void SetText(RoiBase roi, string field, string? invalidText)
        {
            if (invalidText != null)
            {
                InvalidInputs.GetOrCreateValue(roi)[field] = invalidText;
            }
            else if (InvalidInputs.TryGetValue(roi, out var inputs))
            {
                inputs.Remove(field);
                if (inputs.Count == 0)
                {
                    InvalidInputs.Remove(roi);
                }
            }
        }

        public static bool TryGetError(IEnumerable<RoiBase> rois, [NotNullWhen(true)] out string? message)
        {
            foreach (RoiBase roi in rois)
            {
                if (!InvalidInputs.TryGetValue(roi, out var inputs))
                {
                    continue;
                }

                foreach (string field in inputs.Keys)
                {
                    string name = string.IsNullOrWhiteSpace(roi.Label) ? roi.RoiTypeName : roi.Label;
                    message = UiText.Format("StatusInvalidToleranceDraft", name, UiText.Get(field));
                    return true;
                }
            }

            message = null;
            return false;
        }
    }
}
