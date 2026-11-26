global using ImageViewer.Core.Persistence;
global using ImageViewer.Core.Volume;
using System.Runtime.CompilerServices;
using ImageViewer.Common;
using ImageViewer.Models;
using ImageViewer.Plugins;

[assembly: TypeForwardedTo(typeof(PointD))]
[assembly: TypeForwardedTo(typeof(RectD))]
[assembly: TypeForwardedTo(typeof(VectorD))]
[assembly: TypeForwardedTo(typeof(DetectedLineSegment))]
[assembly: TypeForwardedTo(typeof(LineFitGeometry))]
[assembly: TypeForwardedTo(typeof(MeasurementTolerance))]
[assembly: TypeForwardedTo(typeof(RoiPersistenceData))]
[assembly: TypeForwardedTo(typeof(RoiPersistenceCommonData))]
[assembly: TypeForwardedTo(typeof(RoiPersistenceGeometryData))]
[assembly: TypeForwardedTo(typeof(RoiPersistenceMeasurementData))]
[assembly: TypeForwardedTo(typeof(RoiPersistenceOptionsData))]
[assembly: TypeForwardedTo(typeof(RoiPersistencePoint))]
[assembly: TypeForwardedTo(typeof(ImageAnalysisQualityProfile))]
[assembly: TypeForwardedTo(typeof(RoiDetectionStatus))]
[assembly: TypeForwardedTo(typeof(RoiSpecificationStatus))]
[assembly: TypeForwardedTo(typeof(RoiInspectionResult))]
[assembly: TypeForwardedTo(typeof(RoiStatistics))]
[assembly: TypeForwardedTo(typeof(BlobFeature))]
[assembly: TypeForwardedTo(typeof(CameraCalibration))]
[assembly: TypeForwardedTo(typeof(VolumeSliceProvenance))]
[assembly: TypeForwardedTo(typeof(VolumeSliceOrderMetadata))]
[assembly: TypeForwardedTo(typeof(LineFitMode))]
[assembly: TypeForwardedTo(typeof(CaliperEdgePolarity))]

// 2026-09-30 下沉到 ImageViewer.Core 的 ROI 领域基础类型。
// Chinese: 外部插件程序集是按程序集限定名引用这些类型的，改程序集后必须保留转发，
// 否则已编译的第三方插件会在加载时抛 TypeLoadException。
// English: External plugin assemblies reference these by assembly-qualified name, so the moves to Core keep
// forwarders here; without them already-compiled third-party plugins would fail with TypeLoadException.
[assembly: TypeForwardedTo(typeof(BaseViewModel))]
[assembly: TypeForwardedTo(typeof(RoiColor))]
[assembly: TypeForwardedTo(typeof(RoiColors))]
[assembly: TypeForwardedTo(typeof(RoiBase))]
[assembly: TypeForwardedTo(typeof(RoiDisplayNameResolver))]
