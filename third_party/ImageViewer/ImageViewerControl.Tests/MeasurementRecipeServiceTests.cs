using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageViewer.Core.Analysis;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 测量配方读写的守卫测试。
    /// Chinese: 锁住"配方能不能真的复现一套测量方案"：ROI 几何必须跟着配方走（不能只存公差、
    /// 存回来模板是空的）、公差只能有一处出口、未知 ROI 类型不能把数据抹掉、
    /// 以及"载入配方 ≠ 可以出判定"——配方的可判定性必须仍由标定/公差检查决定。
    /// English: Guards that a recipe actually reproduces a measurement plan, and that loading a recipe
    /// never bypasses the calibration gate.
    /// </summary>
    public class MeasurementRecipeServiceTests
    {
        private static RoiPluginRegistry CreateRegistry() => RoiPluginRegistry.CreateBuiltIn();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RecipeKeepsGeometryWithoutCarryingReferenceImageEvidence(bool aligned)
        {
            RoiPluginRegistry registry = CreateRegistry();
            var point = new PointCoordinateMeasureRoi
            {
                Position = new PointD(12, 24), IsEdgeSnapped = true, EdgeScore = 87, EdgeConfidence = 0.94
            };
            var ellipse = new FittedEllipseRoi
            {
                Center = new PointD(20, 30), RadiusX = 8, RadiusY = 4, Angle = 15,
                SourcePointCount = 42, FitInlierCount = 40, FitOutlierCount = 2,
                FitResidualRms = 0.31, FitResidualMedian = 0.22, FitResidualMax = 0.55,
                FitNoiseScale = 0.1, FitAspectRatio = 2, FitAlgorithm = "reference-fit"
            };
            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create("template", null, [point, ellipse],
                0.5, "mm", registry, datumPoints: [new() { X = 10, Y = 20 }, new() { X = 20, Y = 20 }]);

            Assert.False(recipe.Items[0].Template!.IsEdgeSnapped);
            Assert.Equal(0, recipe.Items[1].Template!.FitInlierCount);
            Assert.True(point.IsEdgeSnapped);
            Assert.Equal(40, ellipse.FitInlierCount);

            // Recreate older files that embedded the ROI payload directly, with its original evidence.
            RoiDocument reference = RoiPersistenceService.CreateDocument([point, ellipse], 0.5, "mm", registry);
            recipe.Items[0].Template = reference.Items[0];
            recipe.Items[1].Template = reference.Items[1];
            string legacyJson = MeasurementRecipeService.Serialize(recipe);
            MeasurementRecipeDocument legacy = MeasurementRecipeService.Deserialize(legacyJson);
            RoiDocument restored = aligned
                ? MeasurementRecipeService.BuildAlignedRoiDocument(legacy, new PointD(30, 40), new PointD(30, 50))
                : MeasurementRecipeService.BuildRoiDocument(legacy);
            Assert.Equal(legacyJson, MeasurementRecipeService.Serialize(legacy));

            var rois = RoiPersistenceService.CreateRois(restored, registry).Rois;
            var loadedPoint = Assert.IsType<PointCoordinateMeasureRoi>(rois[0]);
            var loadedEllipse = Assert.IsType<FittedEllipseRoi>(rois[1]);
            Assert.Equal(point.MeasurementId, loadedPoint.MeasurementId);
            Assert.Equal(aligned ? new PointD(26, 42) : point.Position, loadedPoint.Position);
            Assert.Equal(aligned ? new PointD(20, 50) : ellipse.Center, loadedEllipse.Center);
            Assert.Equal(aligned ? 105 : 15, loadedEllipse.Angle);
            Assert.Equal(ellipse.RadiusX, loadedEllipse.RadiusX);
            Assert.Equal(ellipse.RadiusY, loadedEllipse.RadiusY);
            Assert.False(loadedPoint.IsEdgeSnapped);
            Assert.Equal(0, loadedPoint.EdgeScore);
            Assert.Equal(0, loadedPoint.EdgeConfidence);
            Assert.Equal(0, loadedEllipse.SourcePointCount);
            Assert.Equal(0, loadedEllipse.FitInlierCount);
            Assert.Equal(0, loadedEllipse.FitOutlierCount);
            Assert.Equal(0, loadedEllipse.FitResidualRms);
            Assert.Equal(0, loadedEllipse.FitResidualMedian);
            Assert.Equal(0, loadedEllipse.FitResidualMax);
            Assert.Equal(0, loadedEllipse.FitNoiseScale);
            Assert.Equal(0, loadedEllipse.FitAspectRatio);
            Assert.Empty(loadedEllipse.FitAlgorithm);

            string referenceCsv = RoiAnalysisExportService.BuildCsv([point, ellipse], null, 0.5, "mm");
            string loadedCsv = RoiAnalysisExportService.BuildCsv(rois, null, 0.5, "mm");
            Assert.Contains("EdgeConfidence=", referenceCsv, StringComparison.Ordinal);
            Assert.Contains("Inliers=40", referenceCsv, StringComparison.Ordinal);
            Assert.DoesNotContain("EdgeConfidence=", loadedCsv, StringComparison.Ordinal);
            Assert.DoesNotContain("Inliers=", loadedCsv, StringComparison.Ordinal);
            Assert.DoesNotContain("RMS=", loadedCsv, StringComparison.Ordinal);
        }

        [Fact]
        public void DeserializeRejectsNewerRecipeVersion()
        {
            string json = MeasurementRecipeService.Serialize(new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion + 1
            });

            Assert.Throws<NotSupportedException>(() => MeasurementRecipeService.Deserialize(json));
        }

        [Fact]
        public void CreateDetachesCalibrationAndDatumPointsBeforeBackgroundSave()
        {
            var calibration = new CameraCalibration { K1 = 0.01, NormalizationRadius = 100, CalibrationId = "original" };
            var first = new RecipeDatumPoint { Name = "A", X = 10, Y = 20 };
            var second = new RecipeDatumPoint { Name = "B", X = 110, Y = 20 };
            var points = new List<RecipeDatumPoint> { first, second };
            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                "captured", null, [new CircleRoi { Center = new PointD(20, 30), Radius = 5 }],
                0.5, "mm", CreateRegistry(), datumPoints: points, calibration: calibration);

            calibration.K1 = 0.5;
            calibration.CalibrationId = "edited";
            first.X = 999;
            second.Name = "edited";
            points.Clear();

            MeasurementRecipeDocument saved = MeasurementRecipeService.Deserialize(MeasurementRecipeService.Serialize(recipe));
            Assert.Equal(0.01, saved.Calibration!.K1);
            Assert.Equal("original", saved.Calibration.CalibrationId);
            Assert.Equal(2, saved.DatumPoints.Count);
            Assert.Equal(10, saved.DatumPoints[0].X);
            Assert.Equal("B", saved.DatumPoints[1].Name);
            Assert.True(saved.CanLocalize);
        }

        [Fact]
        public void CreateCarriesRoiGeometryIntoTheTemplate()
        {
            RoiPluginRegistry registry = CreateRegistry();
            var circle = new CircleRoi
            {
                Label = "内径 A",
                Center = new PointD(320, 240),
                Radius = 50,
                Tolerance = new MeasurementTolerance { Nominal = 100, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
            };
            var line = new LineMeasureRoi
            {
                Label = "上边缘",
                P1 = new PointD(10, 20),
                P2 = new PointD(110, 20)
            };

            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                "配方A", "机型 A2", [circle, line], 0.05, "mm", registry);

            Assert.Equal(2, recipe.Items.Count);

            // 光有 Key/公差不算配方：没有几何就复现不了任何测量。
            RoiPersistenceData? template = recipe.Items[0].Template;
            Assert.NotNull(template);
            Assert.Equal("内径 A", template.Label);
            RoiPersistencePoint? center = template.Center;
            Assert.NotNull(center);
            Assert.Equal(320, center.X);
            Assert.Equal(240, center.Y);
            Assert.Equal(50, template.Radius);
            Assert.Equal(0.05, recipe.PixelSize);
            Assert.Equal("mm", recipe.PhysicalUnit);

            // 第二项的几何也必须各自对上（按下标配对，不能串行错位）。
            RoiPersistenceData? secondTemplate = recipe.Items[1].Template;
            Assert.NotNull(secondTemplate);
            Assert.Equal("上边缘", secondTemplate.Label);
            RoiPersistencePoint? p2 = secondTemplate.P2;
            Assert.NotNull(p2);
            Assert.Equal(110, p2.X);
        }

        [Fact]
        public void CreateKeepsToleranceInExactlyOnePlace()
        {
            RoiPluginRegistry registry = CreateRegistry();
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(100, 0),
                Tolerance = new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.2, ToleranceMinus = 0.3 }
            };

            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                null, null, [roi], 0.1, "mm", registry);

            MeasurementRecipeItem item = recipe.Items[0];
            MeasurementTolerance? tolerance = item.Tolerance;
            RoiPersistenceData? template = item.Template;
            Assert.NotNull(tolerance);
            Assert.NotNull(template);
            Assert.Equal(5, tolerance.Nominal);

            // 模板里的公差副本必须被清掉：两处都存时改一处另一处还是旧值，无从判断谁生效。
            Assert.Null(template.Common.Tolerance);
        }

        [Fact]
        public void RoundTripThroughFilePreservesGeometryToleranceAndCoordinateSystem()
        {
            RoiPluginRegistry registry = CreateRegistry();
            var roi = new CircleRoi
            {
                Label = "定位孔",
                Center = new PointD(512, 384),
                Radius = 25,
                Tolerance = new MeasurementTolerance { Nominal = 50, TolerancePlus = 0.05, ToleranceMinus = 0.05 }
            };
            var coordinateSystem = new WorkpieceCoordinateSystem
            {
                Origin = new PointD(512, 384),
                AxisX = new VectorD(0, 1),
                Name = "孔系基准"
            };

            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                "配方-往返", "往返测试", [roi], 0.0125, "mm", registry,
                coordinateSystem: coordinateSystem,
                datumPoints:
                [
                    new RecipeDatumPoint { Name = "孔1", X = 0, Y = 0 },
                    new RecipeDatumPoint { Name = "孔2", X = 0, Y = 100 }
                ],
                calibration: new CameraCalibration { K1 = 0.02, NormalizationRadius = 500, CalibrationId = "C-1" });

            string path = Path.Combine(Path.GetTempPath(), $"recipe-{Guid.NewGuid():N}.json");
            try
            {
                MeasurementRecipeService.SaveToFile(path, recipe);
                Assert.True(File.Exists(path));

                MeasurementRecipeDocument loaded = MeasurementRecipeService.LoadFromFile(path);

                Assert.Equal("配方-往返", loaded.Name);
                Assert.Equal(0.0125, loaded.PixelSize);
                Assert.Equal(2, loaded.DatumPoints.Count);
                Assert.True(loaded.CanLocalize);

                // 坐标系是嵌套对象，最容易读回来是 null。
                WorkpieceCoordinateSystem? loadedSystem = loaded.CoordinateSystem;
                Assert.NotNull(loadedSystem);
                Assert.True(loadedSystem.IsDefined);
                Assert.Equal("孔系基准", loadedSystem.Name);
                Assert.Equal(90, Math.Abs(loadedSystem.DatumAngleDegrees), 6);

                CameraCalibration? loadedCalibration = loaded.Calibration;
                Assert.NotNull(loadedCalibration);
                Assert.Equal("C-1", loadedCalibration.CalibrationId);

                RoiPersistenceData? template = loaded.Items[0].Template;
                Assert.NotNull(template);
                Assert.Equal(512, template.Center!.X);
                Assert.Equal(25, template.Radius);

                MeasurementTolerance? tolerance = loaded.Items[0].Tolerance;
                Assert.NotNull(tolerance);
                Assert.Equal(50, tolerance.Nominal);
                Assert.Equal(0.05, tolerance.TolerancePlus);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void RoundTripPreservesDisabledItems()
        {
            RoiPluginRegistry registry = CreateRegistry();
            var enabled = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0), IsVisible = true };
            var disabled = new LineMeasureRoi { P1 = new PointD(0, 5), P2 = new PointD(10, 5), IsVisible = false };

            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                null, null, [enabled, disabled], 0.1, "mm", registry);

            Assert.Equal(2, recipe.Items.Count);
            Assert.Equal(1, recipe.EnabledItemCount);

            string json = MeasurementRecipeService.Serialize(recipe);
            MeasurementRecipeDocument loaded = MeasurementRecipeService.Deserialize(json);

            // 被停用的项若变成启用，会悄悄参与判定并给出原本不存在的 NG。
            Assert.True(loaded.Items[0].IsEnabled);
            Assert.False(loaded.Items[1].IsEnabled);
            Assert.Equal(1, loaded.EnabledItemCount);
        }

        [Fact]
        public void DeserializeRejectsEmptyOrInvalidContent()
        {
            // 静默返回空配方比抛错更危险：操作员会以为配方加载成功，实际一个测量项都没有。
            Assert.Throws<ArgumentException>(() => MeasurementRecipeService.Deserialize("   "));
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => MeasurementRecipeService.Deserialize("null"));
        }

        [Fact]
        public void LoadedRecipeWithoutCalibrationScaleIsBlockedRatherThanAssumedUsable()
        {
            var recipe = new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion,
                Name = "未标定配方",
                // 未标定的表达方式是"比例无效"（<=0 或非有限），1.0 是合法比例、不算未标定。
                PixelSize = 0,
                PhysicalUnit = "px",
                Items =
                {
                    new MeasurementRecipeItem
                    {
                        Key = "line-measure#0",
                        RoiTypeKey = "line-measure",
                        IsEnabled = true,
                        Tolerance = new MeasurementTolerance { Nominal = 5 }
                    }
                }
            };

            MeasurementWorkflowReadiness readiness = MeasurementRecipeService.EvaluateReadiness(recipe);

            // 载入配方不等于可以出物理判定——没有物理比例时必须拦下来。
            Assert.False(recipe.HasValidScale);
            Assert.True(readiness.IsBlocked);
            Assert.False(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(
                readiness.Checks,
                check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationScaleUnavailable);
        }

        [Fact]
        public void EvaluateReadinessPointsOutMissingToleranceOnLoadedRecipe()
        {
            var recipe = new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion,
                PixelSize = 0.05,
                PhysicalUnit = "mm",
                Items =
                {
                    new MeasurementRecipeItem
                    {
                        Key = "circle-measure#0",
                        RoiTypeKey = "circle-measure",
                        IsEnabled = true,
                        // 有值可测但没有公差 ⇒ 能出尺寸、不能出判定。
                        Tolerance = null
                    }
                }
            };

            MeasurementWorkflowReadiness readiness = MeasurementRecipeService.EvaluateReadiness(recipe);

            Assert.False(readiness.IsBlocked);
            Assert.False(readiness.CanProduceSpecificationVerdict);
            Assert.Contains(
                readiness.Checks,
                check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeNoTolerance);
        }

        [Fact]
        public void CreateAcceptsEmptyRoiSetSoARecipeCanBeStartedWithoutItems()
        {
            RoiPluginRegistry registry = CreateRegistry();

            // 空 ROI 集合是合法输入（配方可以先建框架后加项），不应抛错。
            MeasurementRecipeDocument empty = MeasurementRecipeService.Create(
                "空配方", null, [], 0.05, "mm", registry);

            Assert.Empty(empty.Items);
            Assert.Equal(0, empty.EnabledItemCount);
            Assert.False(empty.CanLocalize);
        }

        [Fact]
        public void BuildAlignedRoiDocumentTransformsSupportedGeometryByTranslationAndRotation()
        {
            RoiPluginRegistry registry = CreateRegistry();
            MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                "aligned",
                null,
                [
                    new CircleRoi { Center = new PointD(120, 130), Radius = 12 },
                    new LineMeasureRoi { P1 = new PointD(120, 100), P2 = new PointD(140, 100) },
                    new CaliperMeasureRoi
                    {
                        P1 = new PointD(115, 105), P2 = new PointD(135, 105),
                        CaliperCenter = new PointD(125, 105), CaliperAngleDegrees = 0,
                        HasExplicitCaliperRegion = true, CaliperRegionLength = 20
                    },
                    new EllipseRoi { Center = new PointD(110, 120), RadiusX = 8, RadiusY = 4, Angle = 15 },
                    new PolygonRoi { Points = new System.Collections.ObjectModel.ObservableCollection<PointD> { new(100, 100), new(110, 100), new(110, 110) } }
                ],
                0.25,
                "mm",
                registry,
                datumPoints:
                [
                    new RecipeDatumPoint { Name = "D1", X = 100, Y = 100 },
                    new RecipeDatumPoint { Name = "D2", X = 200, Y = 100 }
                ]);

            // Reference (100,100)->(200,100) maps to current (300,400)->(300,500): +90° and translation.
            RoiDocument aligned = MeasurementRecipeService.BuildAlignedRoiDocument(
                recipe, new PointD(300, 400), new PointD(300, 500));

            Assert.Equal(0.25, aligned.PixelSize);
            Assert.Equal("mm", aligned.PhysicalUnit);
            Assert.Equal(5, aligned.Items.Count);
            Assert.Equal(new PointD(270, 420), new PointD(aligned.Items[0].Center!.X, aligned.Items[0].Center!.Y));
            Assert.Equal(12, aligned.Items[0].Radius);
            Assert.Equal(new PointD(300, 420), new PointD(aligned.Items[1].P1!.X, aligned.Items[1].P1!.Y));
            Assert.Equal(new PointD(300, 440), new PointD(aligned.Items[1].P2!.X, aligned.Items[1].P2!.Y));
            Assert.Equal(new PointD(295, 415), new PointD(aligned.Items[2].P1!.X, aligned.Items[2].P1!.Y));
            Assert.Equal(new PointD(295, 425), new PointD(aligned.Items[2].Center!.X, aligned.Items[2].Center!.Y));
            Assert.Equal(90, aligned.Items[2].Angle);
            Assert.Equal(new PointD(280, 410), new PointD(aligned.Items[3].Center!.X, aligned.Items[3].Center!.Y));
            Assert.Equal(105, aligned.Items[3].Angle);
            Assert.Equal(8, aligned.Items[3].RadiusX);
            Assert.Equal(new PointD(300, 400), new PointD(aligned.Items[4].Points![0].X, aligned.Items[4].Points![0].Y));
        }

        [Fact]
        public void BuildAlignedRoiDocumentRejectsInvalidDatumsAndUnsupportedGeometryWithoutMutatingRecipe()
        {
            var unsupported = new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion,
                PixelSize = 0.5,
                PhysicalUnit = "mm",
                DatumPoints =
                [
                    new RecipeDatumPoint { X = 10, Y = 20 },
                    new RecipeDatumPoint { X = 20, Y = 20 }
                ],
                Items =
                {
                    new MeasurementRecipeItem
                    {
                        Key = "custom#0",
                        RoiTypeKey = "custom-roi",
                        Template = new RoiPersistenceData { Type = "custom-roi", Position = new RoiPersistencePoint { X = 4, Y = 5 } }
                    }
                }
            };
            string originalRecipeJson = MeasurementRecipeService.Serialize(unsupported);

            Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementRecipeService.BuildAlignedRoiDocument(
                unsupported, new PointD(7, 8), new PointD(7, 8)));
            Assert.Throws<NotSupportedException>(() => MeasurementRecipeService.BuildAlignedRoiDocument(
                unsupported, new PointD(30, 40), new PointD(30, 50)));
            Assert.Equal(originalRecipeJson, MeasurementRecipeService.Serialize(unsupported));

            var malformedPolygon = new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion,
                DatumPoints =
                [
                    new RecipeDatumPoint { X = 10, Y = 20 },
                    new RecipeDatumPoint { X = 20, Y = 20 }
                ],
                Items =
                {
                    new MeasurementRecipeItem
                    {
                        Key = "polygon#0",
                        RoiTypeKey = "polygon",
                        Template = new RoiPersistenceData
                        {
                            Type = "polygon",
                            Points = [new RoiPersistencePoint { X = double.NaN, Y = 2 }]
                        }
                    }
                }
            };
            Assert.Throws<InvalidOperationException>(() => MeasurementRecipeService.BuildAlignedRoiDocument(
                malformedPolygon, new PointD(30, 40), new PointD(30, 50)));
        }
    }
}
