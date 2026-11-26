using System;
using System.Runtime.CompilerServices;

namespace ImageViewer.Models
{
    internal static class DualEdgeCaliperDetectionDisplayStateStore
    {
        private static readonly ConditionalWeakTable<CaliperMeasureRoi, DualEdgeCaliperDetectionDisplayState> States = new();

        public static DualEdgeCaliperDetectionDisplayState GetOrCreate(CaliperMeasureRoi roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return States.GetValue(roi, static _ => new DualEdgeCaliperDetectionDisplayState());
        }
    }
}