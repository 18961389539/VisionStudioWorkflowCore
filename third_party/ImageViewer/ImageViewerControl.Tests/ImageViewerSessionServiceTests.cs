using System;
using System.Collections.Generic;
using System.IO;
using ImageViewer.Abstractions;
using ImageViewer.Core.Display;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerSessionServiceTests
    {
        /// <summary>
        /// 构造快照时在"UI 线程侧"完成 ROI→DTO 转换，与 ImageViewerSessionPersistenceWorkflow.CaptureSnapshot 一致。
        /// </summary>
        private static ImageViewerPersistenceSnapshot CreateSnapshot(
            string? imagePath,
            IReadOnlyList<RoiBase> rois,
            double pixelSize = 1.0,
            string physicalUnit = "px",
            CameraCalibration? calibration = null,
            IReadOnlyList<RoiPersistenceData>? unresolvedRois = null,
            ImageAnalysisQualityProfile? qualityProfile = null)
        {
            return new ImageViewerPersistenceSnapshot(
                imagePath,
                RoiPersistenceService.CreateDocument(
                    rois,
                    pixelSize,
                    physicalUnit,
                    RoiPluginRegistry.CreateBuiltIn(),
                    unresolvedRois,
                    qualityProfile),
                1.0,
                0,
                0,
                calibration);
        }

        [Fact]
        public void LoadFromJson_RelativeImagePath_ResolvesAgainstSessionDirectory()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var service = new ImageViewerSessionService();
            string sessionJson = service.SerializeSession("sample", CreateSnapshot("assets/source.png", [], 1.0, "px"), registry);
            string sessionDirectory = Path.Combine(Path.GetTempPath(), "ImageViewerSessionTests", "project");

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, registry, sessionDirectory);

            Assert.Equal(Path.GetFullPath(Path.Combine(sessionDirectory, "assets/source.png")), result.ImagePath);
        }

        [Fact]
        public void LoadFromJson_InvalidJson_ThrowsJsonException()
        {
            var service = new ImageViewerSessionService();

            Assert.Throws<System.Text.Json.JsonException>(() =>
                service.LoadFromJson("{ invalid", RoiPluginRegistry.CreateBuiltIn()));
        }

        [Theory]
        [InlineData("not-a-sha256")]
        [InlineData("")]
        public void LoadFromJson_InvalidImageSha256IsRejected(string digest)
        {
            var service = new ImageViewerSessionService();
            string sessionJson = $$"""{"Version":2,"ImagePath":"source.png","ImageSha256":"{{digest}}"}""";

            Assert.Throws<InvalidDataException>(() => service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn()));
        }

        [Fact]
        public void LoadFromJson_MissingRoiValues_UsesDocumentDefaults()
        {
            var service = new ImageViewerSessionService();
            const string sessionJson = "{\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{}\"}";

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn());

            Assert.Equal(1.0, result.Scale);
            Assert.Equal(1.0, result.PixelSize);
            Assert.Equal("px", result.PhysicalUnit);
        }

        [Fact]
        public void SerializeSession_WithUnresolvedRois_WritesThemBackSoTheySurviveReopen()
        {
            // 场景：工程里含有本机未安装插件才能识别的标注 → 会话重新保存后，这些载荷必须原样保留。
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var service = new ImageViewerSessionService();
            var unresolved = new RoiPersistenceData { Type = "future-plugin-roi", Label = "keep-me" };
            ImageViewerPersistenceSnapshot snapshot = CreateSnapshot(
                null,
                [new CircleRoi()],
                unresolvedRois: [unresolved]);

            string sessionJson = service.SerializeSession("sample", snapshot, registry);
            ImageViewerSessionData reopened = service.LoadFromJson(sessionJson, registry);

            Assert.IsType<CircleRoi>(Assert.Single(reopened.Rois));
            RoiPersistenceData kept = Assert.Single(reopened.UnresolvedRois);
            Assert.Equal("future-plugin-roi", kept.Type);
            Assert.Equal("keep-me", kept.Label);
        }

        [Fact]
        public void SerializeSession_WithCalibration_RoundTripsDistortionParameters()
        {
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var service = new ImageViewerSessionService();
            var calibration = new CameraCalibration
            {
                K1 = 0.05,
                K2 = -0.003,
                K3 = 0.0004,
                TangentialP1 = 0.001,
                TangentialP2 = -0.0007,
                PrincipalX = 200,
                PrincipalY = 150,
                NormalizationRadius = 100,
                ReprojectionErrorRms = 0.08,
                MeasurementUncertaintyPixels = 0.12,
                CalibrationId = "CAL-2026-001"
            };

            string sessionJson = service.SerializeSession("sample", CreateSnapshot(null, [], 0.02, "mm", calibration), registry);
            ImageViewerSessionData result = service.LoadFromJson(sessionJson, registry);

            Assert.NotNull(result.Calibration);
            Assert.Equal(0.05, result.Calibration!.K1, 6);
            Assert.Equal(-0.003, result.Calibration.K2, 6);
            Assert.Equal(200, result.Calibration.PrincipalX, 6);
            Assert.Equal(150, result.Calibration.PrincipalY, 6);
            Assert.Equal(100, result.Calibration.NormalizationRadius, 6);
            Assert.Equal(0.0004, result.Calibration.K3, 6);
            Assert.Equal(0.001, result.Calibration.TangentialP1, 6);
            Assert.Equal(-0.0007, result.Calibration.TangentialP2, 6);
            Assert.Equal(0.08, result.Calibration.ReprojectionErrorRms, 6);
            Assert.Equal(0.12, result.Calibration.MeasurementUncertaintyPixels, 6);
            Assert.Equal("CAL-2026-001", result.Calibration.CalibrationId);
        }

        [Fact]
        public void SerializeSession_RoundTripsProjectQualityProfile()
        {
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var service = new ImageViewerSessionService();
            var profile = new ImageAnalysisQualityProfile
            {
                MaxCaliperScore = 180,
                MinimumDetectionConfidence = 0.12,
                MinimumReviewConfidence = 0.32,
                MinimumReviewValidRatio = 0.64,
                MinimumReviewAngularCoverageDegrees = 210,
                PassConfidence = 0.78,
                PassValidRatio = 0.86,
                PassAngularCoverageDegrees = 300,
                PassResidualRms = 0.9,
                ReviewResidualRms = 2.5
            };

            ImageViewerPersistenceSnapshot snapshot = CreateSnapshot(null, [], qualityProfile: profile);
            string sessionJson = service.SerializeSession("quality-project", snapshot, registry);
            ImageViewerSessionData result = service.LoadFromJson(sessionJson, registry);

            Assert.Equal(profile, result.QualityProfile);
            Assert.Contains("\"QualityProfile\"", sessionJson, StringComparison.Ordinal);
            Assert.Contains("\"PassResidualRms\": 0.9", sessionJson, StringComparison.Ordinal);
        }

        [Fact]
        public void LoadFromJson_LegacySessionHasEmptyPlacement()
        {
            var service = new ImageViewerSessionService();
            ImageViewerSessionData result = service.LoadFromJson("""{"Version":2}""", RoiPluginRegistry.CreateBuiltIn());
            Assert.Null(result.CoordinateSystem);
            Assert.Empty(result.ReferenceDatumPoints);
            Assert.Empty(result.CurrentDatumPoints);
        }

        [Theory]
        [InlineData("""[null]""")]
        [InlineData("""[{"X":1e500,"Y":0}]""")]
        [InlineData("""[{}, {}, {}]""")]
        public void LoadFromJson_RejectsInvalidDatumPoints(string points)
        {
            var service = new ImageViewerSessionService();
            string json = $$"""{"Version":2,"ReferenceDatumPoints":{{points}}}""";
            Assert.Throws<InvalidDataException>(() => service.LoadFromJson(json, RoiPluginRegistry.CreateBuiltIn()));
        }

        [Fact]
        public void SerializeSession_RoundTripsDisplayAdjustment()
        {
            var service = new ImageViewerSessionService();
            var registry = RoiPluginRegistry.CreateBuiltIn();
            var adjustment = DisplayAdjustment.Identity with
            {
                WindowWidth = 0.4,
                WindowLevel = 0.6,
                Brightness = 0.1,
                Contrast = -0.2,
                Gamma = 1.8,
                Invert = true
            };

            ImageViewerPersistenceSnapshot snapshot = CreateSnapshot(null, []) with { Display = adjustment };
            string sessionJson = service.SerializeSession("display", snapshot, registry);
            ImageViewerSessionData result = service.LoadFromJson(sessionJson, registry);

            Assert.Equal(adjustment, result.Display);
            Assert.Contains("\"Display\"", sessionJson, StringComparison.Ordinal);
        }

        [Fact]
        public void LoadFromJson_InvalidDisplayAdjustment_FallsBackToIdentity()
        {
            var service = new ImageViewerSessionService();
            const string sessionJson =
                "{\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{}\",\"Display\":{\"WindowWidth\":0,\"WindowLevel\":1.5}}";

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn());

            Assert.Same(DisplayAdjustment.Identity, result.Display);
        }

        [Fact]
        public void SerializeSession_WritesNestedRoiDocumentObject()
        {
            var service = new ImageViewerSessionService();
            var registry = RoiPluginRegistry.CreateBuiltIn();

            string sessionJson = service.SerializeSession("sample", CreateSnapshot(null, [new CircleRoi { Radius = 15 }], 0.5, "mm"), registry);

            Assert.DoesNotContain("\"RoiDocumentJson\"", sessionJson, StringComparison.Ordinal);
            Assert.Contains("\"RoiDocument\"", sessionJson, StringComparison.Ordinal);
            Assert.Contains("\"Version\": 2", sessionJson, StringComparison.Ordinal);
            Assert.Contains("\"PixelSize\": 0.5", sessionJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Common\"", sessionJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Geometry\"", sessionJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Measurement\"", sessionJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Options\"", sessionJson, StringComparison.Ordinal);
        }

        [Fact]
        public void SerializeSession_ThenLoad_RoundTripsThroughNestedForm()
        {
            var service = new ImageViewerSessionService();
            var registry = RoiPluginRegistry.CreateBuiltIn();

            string sessionJson = service.SerializeSession(
                "sample",
                CreateSnapshot("source.png", [new CircleRoi { Label = "nested", Center = new PointD(5, 6), Radius = 7 }], 0.5, "mm") with
                {
                    Scale = 1.25,
                    TranslateX = 24,
                    TranslateY = -12
                },
                registry);
            ImageViewerSessionData result = service.LoadFromJson(sessionJson, registry);

            Assert.Equal(0.5, result.PixelSize);
            Assert.Equal("mm", result.PhysicalUnit);
            Assert.Equal(1.25, result.Scale);
            Assert.Equal(24, result.TranslateX);
            Assert.Equal(-12, result.TranslateY);
            var circle = Assert.IsType<CircleRoi>(Assert.Single(result.Rois));
            Assert.Equal("nested", circle.Label);
            Assert.Equal(7, circle.Radius);
        }

        /// <summary>
        /// 改造前的会话文件必须继续可读。
        /// Chinese: 该 fixture 由改造前的实现真实生成，ROI 载荷是转义字符串且没有 Version 字段。
        /// English: This fixture was produced by the pre-change implementation; the ROI payload is an escaped
        /// string and the session has no Version field.
        /// </summary>
        [Fact]
        public void LoadFromJson_LegacyEscapedRoiDocumentFixture_RemainsReadable()
        {
            var service = new ImageViewerSessionService();
            string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Session", "legacy-v1-escaped-session.json");
            string sessionBaseDirectory = Path.Combine(Path.GetTempPath(), "ImageViewerSessionTests", "legacy");

            ImageViewerSessionData result = service.LoadFromJson(
                File.ReadAllText(fixturePath),
                RoiPluginRegistry.CreateBuiltIn(),
                sessionBaseDirectory);

            Assert.Equal("legacy-session", result.SessionName);
            Assert.Equal(Path.GetFullPath(Path.Combine(sessionBaseDirectory, "assets/source.png")), result.ImagePath);
            Assert.Equal(0.5, result.PixelSize);
            Assert.Equal("mm", result.PhysicalUnit);
            Assert.Equal(1.25, result.Scale);
            Assert.Equal(24, result.TranslateX);
            Assert.Equal(-12, result.TranslateY);
            var circle = Assert.IsType<CircleRoi>(Assert.Single(result.Rois));
            Assert.Equal("legacy-circle", circle.Label);
            Assert.Equal(new PointD(120, 84), circle.Center);
            Assert.Equal(15, circle.Radius);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(99)]
        public void LoadFromJson_FutureSessionVersion_ThrowsNotSupportedException(int version)
        {
            var service = new ImageViewerSessionService();
            string sessionJson = $"{{\"Version\":{version},\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{{}}\"}}";

            Assert.Throws<NotSupportedException>(() =>
                service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void LoadFromJson_SupportedOrNonPositiveSessionVersion_IsAccepted(int version)
        {
            var service = new ImageViewerSessionService();
            string sessionJson = $"{{\"Version\":{version},\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{{}}\"}}";

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn());

            Assert.Equal("sample", result.SessionName);
        }

        [Fact]
        public void LoadFromJson_MissingSessionVersion_IsAcceptedAsLegacy()
        {
            var service = new ImageViewerSessionService();
            const string sessionJson = "{\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{}\"}";

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn());

            Assert.Equal("sample", result.SessionName);
        }

        [Fact]
        public void LoadFromJson_WithoutCalibration_ReturnsNullCalibration()
        {
            var service = new ImageViewerSessionService();
            const string sessionJson = "{\"SessionName\":\"sample\",\"RoiDocumentJson\":\"{}\"}";

            ImageViewerSessionData result = service.LoadFromJson(sessionJson, RoiPluginRegistry.CreateBuiltIn());

            Assert.Null(result.Calibration);
        }
    }
}
