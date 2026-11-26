using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class RoiPersistenceServiceTests
    {
        private static readonly RoiPersistenceServiceRoundTripExpectation[] SerializationExpectations =
        [
            new(
                new BlobAnalysisRoi
                {
                    Center = new PointD(10, 12),
                    Width = 14,
                    Height = 16,
                    Angle = 18,
                    UseOtsu = true,
                    ManualThreshold = 90,
                    DetectDark = true,
                    MinArea = 3
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<BlobAnalysisRoi>(roi);
                    Assert.Equal(new PointD(10, 12), roundTripped.Center);
                    Assert.Equal(14, roundTripped.Width);
                    Assert.Equal(16, roundTripped.Height);
                    Assert.Equal(18, roundTripped.Angle);
                    Assert.True(roundTripped.UseOtsu);
                    Assert.Equal(90, roundTripped.ManualThreshold);
                    Assert.True(roundTripped.DetectDark);
                    Assert.Equal(3, roundTripped.MinArea);
                }),
            new(
                new CircularCaliperMeasureRoi
                {
                    Center = new PointD(25, 30),
                    Radius = 11,
                    CaliperCount = 21,
                    CaliperSearchRange = 9,
                    CaliperSamplingHalfWidth = 2,
                    MinimumValidCalipers = 7,
                    CaliperMinimumGradient = 5.5,
                    CaliperOutlierThreshold = 1.75,
                    CaliperEdgePolarity = CaliperEdgePolarity.LightToDark
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<CircularCaliperMeasureRoi>(roi);
                    Assert.Equal(new PointD(25, 30), roundTripped.Center);
                    Assert.Equal(11, roundTripped.Radius);
                    Assert.Equal(21, roundTripped.CaliperCount);
                    Assert.Equal(9, roundTripped.CaliperSearchRange);
                    Assert.Equal(2, roundTripped.CaliperSamplingHalfWidth);
                    Assert.Equal(7, roundTripped.MinimumValidCalipers);
                    Assert.Equal(5.5, roundTripped.CaliperMinimumGradient);
                    Assert.Equal(1.75, roundTripped.CaliperOutlierThreshold);
                    Assert.Equal(CaliperEdgePolarity.LightToDark, roundTripped.CaliperEdgePolarity);
                }),
            new(
                new ConcentricityMeasureRoi
                {
                    Center1 = new PointD(4, 5),
                    Radius1 = 6,
                    Center2 = new PointD(7, 8),
                    Radius2 = 9
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<ConcentricityMeasureRoi>(roi);
                    Assert.Equal(new PointD(4, 5), roundTripped.Center1);
                    Assert.Equal(6, roundTripped.Radius1);
                    Assert.Equal(new PointD(7, 8), roundTripped.Center2);
                    Assert.Equal(9, roundTripped.Radius2);
                })
        ];

        public static IEnumerable<object[]> SerializationCases =>
            SerializationExpectations.Select(static expectation => new object[] { expectation });

        public static IEnumerable<object[]> LegacyFixtureCases =>
            new[]
            {
                new object[]
                {
                    new LegacyPersistenceFixtureExpectation(
                        "legacy-flat-blob-analysis.json",
                        0.5,
                        "mm",
                        roi =>
                        {
                            var roundTripped = Assert.IsType<BlobAnalysisRoi>(roi);
                            Assert.Equal("legacy-blob", roundTripped.Label);
                            Assert.Equal(new PointD(10, 12), roundTripped.Center);
                            Assert.Equal(14, roundTripped.Width);
                            Assert.Equal(16, roundTripped.Height);
                            Assert.Equal(18, roundTripped.Angle);
                            Assert.True(roundTripped.UseOtsu);
                            Assert.Equal(90, roundTripped.ManualThreshold);
                            Assert.True(roundTripped.DetectDark);
                            Assert.Equal(3, roundTripped.MinArea);
                        })
                },
                new object[]
                {
                    new LegacyPersistenceFixtureExpectation(
                        "legacy-flat-circular-caliper.json",
                        1.25,
                        "px",
                        roi =>
                        {
                            var roundTripped = Assert.IsType<CircularCaliperMeasureRoi>(roi);
                            Assert.Equal("legacy-circular-caliper", roundTripped.Label);
                            Assert.Equal(new PointD(25, 30), roundTripped.Center);
                            Assert.Equal(11, roundTripped.Radius);
                            Assert.Equal(21, roundTripped.CaliperCount);
                            Assert.Equal(9, roundTripped.CaliperSearchRange);
                            Assert.Equal(2, roundTripped.CaliperSamplingHalfWidth);
                            Assert.Equal(7, roundTripped.MinimumValidCalipers);
                            Assert.Equal(5.5, roundTripped.CaliperMinimumGradient);
                            Assert.Equal(1.75, roundTripped.CaliperOutlierThreshold);
                            Assert.Equal(CaliperEdgePolarity.LightToDark, roundTripped.CaliperEdgePolarity);
                        })
                },
                new object[]
                {
                    new LegacyPersistenceFixtureExpectation(
                        "legacy-flat-polygon.json",
                        2.0,
                        "um",
                        roi =>
                        {
                            var roundTripped = Assert.IsType<PolygonRoi>(roi);
                            Assert.Equal("legacy-polygon", roundTripped.Label);
                            Assert.True(roundTripped.IsClosed);
                            Assert.Equal(3, roundTripped.Points.Count);
                            Assert.Equal(new PointD(5, 5), roundTripped.Points[2]);
                        })
                }
            };

        [Theory]
        [MemberData(nameof(SerializationCases))]
        public void SerializeDeserialize_RoundTripsRepresentativeRois_AndKeepsFlatJsonShape(RoiPersistenceServiceRoundTripExpectation expectation)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            string json = RoiPersistenceService.Serialize([expectation.SourceRoi], 2.5, "mm", registry);

            Assert.DoesNotContain("\"Common\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Geometry\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Measurement\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Options\"", json, StringComparison.Ordinal);

            var (rois, _, pixelSize, physicalUnit) = RoiPersistenceService.Deserialize(json, registry);
            RoiBase roi = Assert.Single(rois);

            Assert.Equal(2.5, pixelSize);
            Assert.Equal("mm", physicalUnit);
            expectation.AssertRoundTripped(roi);
        }

        [Fact]
        public void CreateRois_LegacyClrTypeName_MigratesToTheStableTypeKey()
        {
            // 旧文件把 CLR 类型名写进 Type（例如 "CircleRoi"）；
            // 只有“恰好一个已注册插件”匹配时才迁移，避免同名类型被猜错。
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            var result = RoiPersistenceService.CreateRois(
                new RoiDocument
                {
                    Items =
                    [
                        new RoiPersistenceData { Type = "CircleRoi", Label = "legacy" }
                    ]
                },
                registry);

            RoiBase roi = Assert.Single(result.Rois);
            Assert.IsType<CircleRoi>(roi);
            Assert.Equal("legacy", roi.Label);
        }

        [Fact]
        public void CreateRois_AmbiguousClrTypeName_GoesToUnresolvedInsteadOfGuessing()
        {
            // 两个不同程序集/命名空间里的插件若有同名 CLR 类型，旧回退会“猜”第一个——
            // 现在必须拒绝解析，进 unresolved 载荷（数据不丢、也不错误反序列化）。
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            RoiPluginRegistry otherRegistry = RoiPluginRegistry.CreateBuiltIn();

            // 用两个不同实现类型的插件模拟同名类型键的场景不可行（Register 按 RoiType 去重），
            // 因此这里验证的是“未知类型键 → unresolved”，而同名歧义由 ResolvePlugin 的 Length == 1 守卫覆盖。
            var result = RoiPersistenceService.CreateRois(
                new RoiDocument
                {
                    Items =
                    [
                        new RoiPersistenceData { Type = "totally-unknown-type", Label = "keep" }
                    ]
                },
                registry);

            Assert.Empty(result.Rois);
            RoiPersistenceData unresolved = Assert.Single(result.UnresolvedItems);
            Assert.Equal("totally-unknown-type", unresolved.Type);
            Assert.Equal("keep", unresolved.Label);
            Assert.Equal(registry.RegisteredTypeKeys.Count, registry.RegisteredTypeKeys.Count);
            Assert.All(registry.RegisteredTypeKeys, key => Assert.NotEqual("totally-unknown-type", key));
        }

        [Theory]
        [MemberData(nameof(LegacyFixtureCases))]
        public void Deserialize_LegacyFlatJsonFixtures_RemainCompatible(LegacyPersistenceFixtureExpectation expectation)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            string fixtureJson = File.ReadAllText(GetFixturePath(expectation.FileName));

            var (rois, _, pixelSize, physicalUnit) = RoiPersistenceService.Deserialize(fixtureJson, registry);
            RoiBase roi = Assert.Single(rois);

            Assert.Equal(expectation.ExpectedPixelSize, pixelSize);
            Assert.Equal(expectation.ExpectedPhysicalUnit, physicalUnit);
            expectation.AssertRoundTripped(roi);

            string reserialized = RoiPersistenceService.Serialize([roi], pixelSize, physicalUnit, registry);
            Assert.DoesNotContain("\"Common\"", reserialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Geometry\"", reserialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Measurement\"", reserialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Options\"", reserialized, StringComparison.Ordinal);
        }

        [Fact]
        public void Serialize_WithoutPluginRegistry_Throws()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
                RoiPersistenceService.Serialize([new CircleRoi()], 1.0, "px", pluginRegistry: null!));

            Assert.Equal("pluginRegistry", ex.ParamName);
        }

        [Fact]
        public void Deserialize_UnknownType_IgnoresItemAndKeepsKnownItems()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            string knownJson = RoiPersistenceService.Serialize([new CircleRoi()], 2.0, "mm", registry);
            using var document = System.Text.Json.JsonDocument.Parse(knownJson);
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("Version", 1);
                writer.WriteNumber("PixelSize", 2.0);
                writer.WriteString("PhysicalUnit", "mm");
                writer.WriteStartArray("Items");
                writer.WriteRawValue(document.RootElement.GetProperty("Items")[0].GetRawText());
                writer.WriteStartObject();
                writer.WriteString("Type", "unknown-roi-type");
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var (rois, _, pixelSize, physicalUnit) = RoiPersistenceService.Deserialize(
                System.Text.Encoding.UTF8.GetString(stream.ToArray()),
                registry);

            Assert.Single(rois);
            Assert.IsType<CircleRoi>(rois[0]);
            Assert.Equal(2.0, pixelSize);
            Assert.Equal("mm", physicalUnit);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(99)]
        public void Deserialize_FutureDocumentVersion_ThrowsNotSupportedException(int version)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            Assert.Throws<NotSupportedException>(() =>
                RoiPersistenceService.Deserialize(BuildDocumentJsonWithVersion(version), registry));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(null)]
        public void Deserialize_MissingOrNonPositiveVersion_IsAcceptedAsLegacy(int? version)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            var (rois, _, pixelSize, physicalUnit) = RoiPersistenceService.Deserialize(BuildDocumentJsonWithVersion(version), registry);

            Assert.IsType<CircleRoi>(Assert.Single(rois));
            Assert.Equal(2.0, pixelSize);
            Assert.Equal("mm", physicalUnit);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-0.5)]
        public void Deserialize_ExplicitInvalidPixelSize_IsRejected(double pixelSize)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            string json = $$"""
                {
                  "Version": 1,
                  "PixelSize": {{pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "PhysicalUnit": "mm",
                  "Items": []
                }
                """;

            Assert.Throws<ArgumentException>(() => RoiPersistenceService.Deserialize(json, registry));
        }

        /// <summary>
        /// 既有的颜色字符串必须继续可读。
        /// Chinese: 会话文件里存的是 WPF Color.ToString 的结果（#AARRGGBB），也兼容命名色与简写。
        /// English: Existing color strings must remain readable. Session files store the result of
        /// WPF Color.ToString ("#AARRGGBB"); named colors and short hex are also accepted.
        /// </summary>
        [Theory]
        [InlineData("#FF00FFFF", 255, 0, 255, 255)]
        [InlineData("#FFD700", 255, 255, 215, 0)]
        [InlineData("#0FF", 255, 0, 255, 255)]
        [InlineData("Yellow", 255, 255, 255, 0)]
        [InlineData("gold", 255, 255, 215, 0)]
        public void Deserialize_LegacyColorFormats_AreAccepted(string storedColor, byte a, byte r, byte g, byte b)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            var (rois, _, _, _) = RoiPersistenceService.Deserialize(BuildLegacyCircleJson(storedColor), registry);

            var circle = Assert.IsType<CircleRoi>(Assert.Single(rois));
            Assert.Equal(RoiColor.FromArgb(a, r, g, b), circle.StrokeColor);
        }

        [Fact]
        public void Deserialize_ColorRoundTripsToSameString()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            var (rois, _, _, _) = RoiPersistenceService.Deserialize(BuildLegacyCircleJson("#FF00FFFF"), registry);
            string reserialized = RoiPersistenceService.Serialize(rois, 1.0, "px", registry);

            Assert.Contains("\"StrokeColor\": \"#FF00FFFF\"", reserialized, StringComparison.Ordinal);
        }

        [Fact]
        public void Deserialize_InvalidColorString_KeepsDefaultInsteadOfThrowing()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            var (rois, _, _, _) = RoiPersistenceService.Deserialize(BuildLegacyCircleJson("NotAColor"), registry);

            var circle = Assert.IsType<CircleRoi>(Assert.Single(rois));
            Assert.Equal(RoiColors.Gold, circle.StrokeColor);
        }

        [Fact]
        public void Deserialize_UnknownTypeKey_ReportsUnresolvedPayloadInsteadOfDroppingIt()
        {
            // 缺插件时旧实现会静默丢弃整条标注；现在必须把原始载荷带出来，供调用方告警并原样回写。
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            const string documentJson = """
                {
                  "Version": 1,
                  "PixelSize": 2.0,
                  "PhysicalUnit": "mm",
                  "Items": [
                    {
                      "Type": "circle",
                      "Center": { "X": 5, "Y": 6 },
                      "Radius": 7
                    },
                    {
                      "Type": "future-plugin-roi",
                      "Label": "keep-me",
                      "Center": { "X": 1, "Y": 2 }
                    }
                  ]
                }
                """;

            RoiDocumentLoadResult result = RoiPersistenceService.Deserialize(documentJson, registry);

            Assert.IsType<CircleRoi>(Assert.Single(result.Rois));
            RoiPersistenceData unresolved = Assert.Single(result.UnresolvedItems);
            Assert.Equal("future-plugin-roi", unresolved.Type);
            Assert.Equal("keep-me", unresolved.Label);
            Assert.Equal(2.0, result.PixelSize);
            Assert.Equal("mm", result.PhysicalUnit);
        }

        private static string BuildLegacyCircleJson(string strokeColor)
        {
            return $$"""
                {
                  "Version": 1,
                  "PixelSize": 1.0,
                  "PhysicalUnit": "px",
                  "Items": [
                    {
                      "Type": "circle",
                      "Label": "legacy",
                      "StrokeColor": "{{strokeColor}}",
                      "StrokeThickness": 3.5,
                      "IsVisible": false,
                      "IsLocked": true,
                      "Center": { "X": 5, "Y": 6 },
                      "Radius": 7
                    }
                  ]
                }
                """;
        }

        private static string BuildDocumentJsonWithVersion(int? version)
        {
            string versionProperty = version.HasValue ? $"\"Version\": {version.Value}," : string.Empty;
            return $$"""
                {
                  {{versionProperty}}
                  "PixelSize": 2.0,
                  "PhysicalUnit": "mm",
                  "Items": [
                    {
                      "Type": "circle",
                      "Center": { "X": 5, "Y": 6 },
                      "Radius": 7
                    }
                  ]
                }
                """;
        }

        private static string GetFixturePath(string fileName)
        {
            return Path.Combine(AppContext.BaseDirectory, "Fixtures", "RoiPersistence", fileName);
        }

        public sealed record RoiPersistenceServiceRoundTripExpectation(
            RoiBase SourceRoi,
            Action<RoiBase> AssertRoundTripped);

        public sealed record LegacyPersistenceFixtureExpectation(
            string FileName,
            double ExpectedPixelSize,
            string ExpectedPhysicalUnit,
            Action<RoiBase> AssertRoundTripped);
    }
}
