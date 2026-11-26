#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ImageViewer.Core.Display;
using ImageViewer.Core.Geometry;
using ImageViewer.Models;

namespace ImageViewer.Core.Persistence
{
    /// <summary>Portable session envelope shared by persistence and UI hosts.</summary>
    public sealed class ImageViewerSessionDocument
    {
        /// <summary>
        /// 会话文档结构版本。
        /// Chinese: 描述会话"信封"结构；缺失或非正数视为早期文件，按当前版本宽容处理。
        /// English: Describes the session envelope layout. Missing or non-positive values are treated as early files.
        /// </summary>
        public int Version { get; set; }

        public string? SessionName { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public string? ImagePath { get; set; }
        /// <summary>Optional SHA-256 of the exact bytes referenced by ImagePath.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageSha256 { get; set; }
        [JsonConverter(typeof(RoiDocumentJsonConverter))]
        public RoiDocument? RoiDocument { get; set; }

        /// <summary>
        /// 历史字段名兼容入口，只读不写。
        /// Chinese: 改造前的会话文件把 ROI 载荷放在 "RoiDocumentJson" 下（转义字符串）。读取时转交给
        /// RoiDocument；写出时该属性恒为 null，配合 WhenWritingNull 保证不再出现在新文件里。
        /// English: Legacy field name, read-only. Pre-change session files stored the ROI payload under
        /// "RoiDocumentJson" as an escaped string. Reads forward to RoiDocument; the getter always returns null
        /// so WhenWritingNull keeps it out of newly written files.
        /// </summary>
        [JsonPropertyName("RoiDocumentJson")]
        [JsonConverter(typeof(RoiDocumentJsonConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RoiDocument? LegacyRoiDocument
        {
            get => null;
            set => RoiDocument ??= value;
        }

        public double Scale { get; set; } = 1.0;
        public double TranslateX { get; set; }
        public double TranslateY { get; set; }
        public CameraCalibration? Calibration { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkpieceCoordinateSystem? CoordinateSystem { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RecipeDatumPoint>? ReferenceDatumPoints { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RecipeDatumPoint>? CurrentDatumPoints { get; set; }

        /// <summary>
        /// 显示调节（窗宽窗位/亮度/对比度/伽马/反色）。
        /// Chinese: 旧文件没有这一项 → 读出 null → 按默认参数（透传）处理，因此不需要提升会话版本号。
        /// 伪彩色调色板不在这里：它定义在 WPF 侧，框架中立层拿不到那个枚举。
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DisplayAdjustment? Display { get; set; }
    }
}
#pragma warning restore CS1591
