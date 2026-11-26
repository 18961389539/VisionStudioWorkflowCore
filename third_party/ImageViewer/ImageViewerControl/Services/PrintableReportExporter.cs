using System;
using System.IO;
using System.Windows.Documents;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;

namespace ImageViewer.Services;

/// <summary>
/// 可打印报告的落盘器。
/// Chinese: 选 XPS 而不是 PDF，是因为 XPS 写出一整份固定版式文档不依赖任何第三方包，
/// 且 Windows 自带阅读器；需要 PDF 时可由系统打印驱动转换。
/// English: XPS keeps the feature dependency-free while still producing a fixed-layout deliverable.
/// </summary>
internal static class PrintableReportExporter
{
    /// <summary>把文档写成 XPS 文件（覆盖已有文件）。</summary>
    public static void ExportToXps(FlowDocument document, string filePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var package = new XpsDocument(fullPath, FileAccess.ReadWrite);
        XpsDocumentWriter writer = XpsDocument.CreateXpsDocumentWriter(package);
        writer.Write(((IDocumentPaginatorSource)document).DocumentPaginator);
    }
}
