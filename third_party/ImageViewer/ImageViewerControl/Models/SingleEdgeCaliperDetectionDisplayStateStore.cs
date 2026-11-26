using System;
using System.Runtime.CompilerServices;

namespace ImageViewer.Models
{
    internal static class SingleEdgeCaliperDetectionDisplayStateStore
    {
        private static readonly ConditionalWeakTable<ISingleEdgeCaliperDetectionDisplayStateOwner, SingleEdgeCaliperDetectionDisplayState> States = new();

        public static SingleEdgeCaliperDetectionDisplayState GetOrCreate(ISingleEdgeCaliperDetectionDisplayStateOwner owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            return States.GetValue(owner, static _ => new SingleEdgeCaliperDetectionDisplayState());
        }
    }
}