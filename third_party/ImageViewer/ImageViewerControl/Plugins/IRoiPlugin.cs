using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.ViewModels;

namespace ImageViewer.Plugins
{
    public interface IRoiPlugin :
        IRoiPluginDefinition,
        IRoiCollectionCapability,
        IRoiPersistenceCapability,
        IRoiInfoCapability,
        IRoiEditorCapability
    {
    }
}
