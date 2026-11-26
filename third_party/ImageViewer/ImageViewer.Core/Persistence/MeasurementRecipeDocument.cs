#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using ImageViewer.Core.Geometry;
using ImageViewer.Models;

namespace ImageViewer.Core.Persistence
{
    /// <summary>
    /// 测量配方中的单个测量项定义（参考图像坐标下的 ROI 模板 + 公差）。
    /// Chinese: 模板几何保留在保存配方时的参考图像坐标；执行配方时，操作员在参考图与当前图上
    /// 分别点选两个基准点，由刚体变换把模板映射到当前图像坐标。
    /// English: Template geometry stays in the reference image coordinates and is rigidly mapped to the current
    /// image from two operator-selected datum points.
    /// </summary>
    public sealed class MeasurementRecipeItem
    {
        /// <summary>稳定键：配方内唯一，用于跨会话匹配测量结果与重复性统计。</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>显示名（操作员看到的项目名，例如"内径 A"）。</summary>
        public string? DisplayName { get; set; }

        /// <summary>量名（例如"半径"/"弧长"/"平行度角度偏差"）。</summary>
        public string? Quantity { get; set; }

        /// <summary>ROI 类型注册键（稳定 TypeKey，不依赖 CLR 类型名）。</summary>
        public string? RoiTypeKey { get; set; }

        /// <summary>
        /// 参考图像坐标下的 ROI 模板载荷（由宿主序列化）。
        /// Chinese: 只放**几何与算子参数**，不放公差——公差只在 <see cref="Tolerance"/> 一处存放。
        /// 曾经把公差同时写进模板的 Common.Tolerance，导致改公差时要改两处、且两者冲突时无从判断谁生效。
        /// </summary>
        public RoiPersistenceData? Template { get; set; }

        /// <summary>公差配置。</summary>
        public MeasurementTolerance? Tolerance { get; set; }

        /// <summary>该测量项是否启用（禁用项不参与测量与判定）。</summary>
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// 测量配方：坐标系 + 测量项 + 标定。
    /// Chinese: 配方是"选择配方 → 标定检查 → 测量 → 复核 → 出报告"里的第一步产物。
    /// 它把散落在会话里的 ROI、公差、标定收进一个带版本号的可交付文件，使同一个测量方案
    /// 能在不同工件、不同时间、不同机器上复现。
    /// English: Bundles coordinate system, items and calibration into a versioned deliverable so the same
    /// measurement plan can be reproduced across workpieces, sessions and machines.
    /// </summary>
    public sealed class MeasurementRecipeDocument
    {
        /// <summary>配方文档当前结构版本。</summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// 配方结构版本。
        /// Chinese: 与 <see cref="ImageViewerSessionDocument.Version"/> 同理，缺失/非正数按早期文件宽容处理。
        /// </summary>
        public int Version { get; set; }

        /// <summary>配方名称。</summary>
        public string? Name { get; set; }

        /// <summary>配方描述/备注（工艺信息、适用机型等）。</summary>
        public string? Description { get; set; }

        public DateTimeOffset SavedAtUtc { get; set; }

        /// <summary>
        /// 保存配方时的工件坐标系描述（可空）。
        /// Chinese: 实际对齐以 <see cref="DatumPoints"/> 中保存的参考图像坐标和操作员当前选取的
        /// 两个图像点计算；尚未提供自动特征检测。
        /// </summary>
        public WorkpieceCoordinateSystem? CoordinateSystem { get; set; }

        /// <summary>保存配方时参考图像上的两个基准点坐标，按 1、2 顺序排列。</summary>
        public List<RecipeDatumPoint> DatumPoints { get; set; } = new();

        /// <summary>标定（比例由 PixelSize 表达、畸变由 CameraCalibration 表达）。</summary>
        public double PixelSize { get; set; } = 1.0;

        public string? PhysicalUnit { get; set; }

        public CameraCalibration? Calibration { get; set; }

        /// <summary>检测质量门限；旧配方缺少此项时沿用查看器当前设置。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImageAnalysisQualityProfile? QualityProfile { get; set; }

        /// <summary>测量项。</summary>
        public List<MeasurementRecipeItem> Items { get; set; } = new();

        /// <summary>参与测量的项数（启用项）。</summary>
        [JsonIgnore]
        public int EnabledItemCount => Items.Count(item => item.IsEnabled);

        /// <summary>
        /// 配方是否包含可用于手动刚体对齐的基准点对。
        /// Chinese: 两个不重合的参考点与当前图上对应点共同确定平移和旋转；不执行自动检测。
        /// English: Two distinct reference points enable operator-guided rigid alignment; this does not detect features.
        /// </summary>
        [JsonIgnore]
        public bool CanLocalize
        {
            get
            {
                if (DatumPoints is null || DatumPoints.Count < 2
                    || DatumPoints[0] is null || DatumPoints[1] is null)
                {
                    return false;
                }

                return double.IsFinite(DatumPoints[0].X) && double.IsFinite(DatumPoints[0].Y)
                    && double.IsFinite(DatumPoints[1].X) && double.IsFinite(DatumPoints[1].Y)
                    && (Math.Abs(DatumPoints[1].X - DatumPoints[0].X) > 1e-12
                        || Math.Abs(DatumPoints[1].Y - DatumPoints[0].Y) > 1e-12);
            }
        }

        /// <summary>有效像素比例（&gt; 0 且有限）。</summary>
        [JsonIgnore]
        public bool HasValidScale => double.IsFinite(PixelSize) && PixelSize > 0;
    }

    /// <summary>
    /// 配方中的手动参考图像基准点。
    /// Chinese: 两个点按稳定顺序保存。执行配方时，操作员在当前图像上重新选取对应的两个点。
    /// </summary>
    public sealed class RecipeDatumPoint
    {
        /// <summary>基准点名称。</summary>
        public string? Name { get; set; }

        /// <summary>参考图像中的 X 坐标。</summary>
        public double X { get; set; }

        /// <summary>参考图像中的 Y 坐标。</summary>
        public double Y { get; set; }
    }
}
#pragma warning restore CS1591
