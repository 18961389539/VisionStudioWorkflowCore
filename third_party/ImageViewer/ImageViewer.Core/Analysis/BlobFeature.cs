#pragma warning disable CS1591

namespace ImageViewer.Models
{
    /// <summary>
    /// 连通域（斑点）特征。
    /// Chinese: 斑点分析的单个结果项，属于模型层的数据记录（早期放在 Services 下，导致模型成员反向依赖服务层）；
    /// 坐标一律使用 PointD / RectD，不引用 WPF。Contour 为像素边界多边形（格点坐标，闭合但首尾点不重复），
    /// 需要轮廓时由 BlobAlgorithm.Connect 填充，否则为 null。
    /// English: A single blob-analysis result item. It belongs to the model layer (it was previously declared under
    /// Services, which made a model member depend on the service layer) and uses PointD / RectD exclusively.
    /// Contour holds the traced pixel-boundary polygon on the grid coordinates; it is null unless contour
    /// tracing was requested.
    /// </summary>
    public readonly record struct BlobFeature(
        int Label,
        int Area,
        PointD Centroid,
        RectD BoundingBox,
        PointD[]? Contour = null
    );
}

#pragma warning restore CS1591
