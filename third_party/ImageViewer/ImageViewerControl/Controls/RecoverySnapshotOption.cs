namespace ImageViewer.Controls;

internal sealed record RecoverySnapshotOption(string Path, DateTime LastWriteUtc, string? ProjectName, string? ImageName);
