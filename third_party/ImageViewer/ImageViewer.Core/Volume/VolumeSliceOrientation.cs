namespace ImageViewer.Core.Volume;

/// <summary>Coordinate orientation used by 2D slice, MPR, and 3D plane projections.</summary>
public enum VolumeSliceOrientation
{
    /// <summary>View along the depth axis.</summary>
    Axial,
    /// <summary>View along the row axis.</summary>
    Coronal,
    /// <summary>View along the column axis.</summary>
    Sagittal
}
