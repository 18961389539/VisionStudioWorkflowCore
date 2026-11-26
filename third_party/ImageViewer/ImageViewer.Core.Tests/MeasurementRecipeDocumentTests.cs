using System;
using System.Text.Json;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// 测量配方文档守卫测试。
    /// Chinese: 关注三件会让"配方复现不了"的退化行为：
    /// ① 序列化往返必须无损（含嵌套的坐标系与标定）；② 只有 1 个/重合基准点时
    /// <see cref="MeasurementRecipeDocument.CanLocalize"/> 必须为 false（否则会拿着一个只能定原点、
    /// 定不了基准轴的"半成品"去做自动定位，测量结果整体错位却看起来正常）；
    /// ③ 版本号与比例校验必须存在，缺比例不能被静默当成 1px=1mm。
    /// </summary>
    [Trait("Category", "Unit")]
    public sealed class MeasurementRecipeDocumentTests
    {
        private static MeasurementRecipeDocument CreateSample()
        {
            return new MeasurementRecipeDocument
            {
                Version = MeasurementRecipeDocument.CurrentVersion,
                Name = "上盖内径检测",
                Description = "适用机型 A2；基准为两处定位孔",
                SavedAtUtc = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero),
                CoordinateSystem = new WorkpieceCoordinateSystem
                {
                    Origin = new PointD(512, 384),
                    AxisX = new VectorD(100, 20),
                    Name = "孔系基准"
                },
                DatumPoints =
                {
                    new RecipeDatumPoint { Name = "孔1", X = 0, Y = 0 },
                    new RecipeDatumPoint { Name = "孔2", X = 100, Y = 20 }
                },
                PixelSize = 0.0125,
                PhysicalUnit = "mm",
                Calibration = new CameraCalibration
                {
                    PrincipalX = 640,
                    PrincipalY = 512,
                    K1 = -0.12,
                    K2 = 0.03,
                    NormalizationRadius = 800,
                    ReprojectionErrorRms = 0.34,
                    MeasurementUncertaintyPixels = 0.08,
                    CalibrationId = "CAL-2026-10"
                },
                Items =
                {
                    new MeasurementRecipeItem
                    {
                        Key = "circle-measure#0",
                        DisplayName = "内径 A",
                        Quantity = "直径",
                        RoiTypeKey = "circle-measure",
                        IsEnabled = true,
                        Tolerance = new MeasurementTolerance { Nominal = 20.0, TolerancePlus = 0.05, ToleranceMinus = 0.05 }
                    },
                    new MeasurementRecipeItem
                    {
                        Key = "line-measure#1",
                        DisplayName = "边缘直线度",
                        Quantity = "直线度",
                        RoiTypeKey = "line-measure",
                        IsEnabled = false
                    }
                }
            };
        }

        [Fact]
        public void RoundTripThroughJsonPreservesEveryField()
        {
            MeasurementRecipeDocument original = CreateSample();

            string json = JsonSerializer.Serialize(
                original,
                ImageViewerCoreJsonSerializationContext.Default.MeasurementRecipeDocument);
            MeasurementRecipeDocument restored = JsonSerializer.Deserialize(
                json,
                ImageViewerCoreJsonSerializationContext.Default.MeasurementRecipeDocument)!;

            Assert.Equal(original.Version, restored.Version);
            Assert.Equal(original.Name, restored.Name);
            Assert.Equal(original.Description, restored.Description);
            Assert.Equal(original.SavedAtUtc, restored.SavedAtUtc);
            Assert.Equal(original.PixelSize, restored.PixelSize);
            Assert.Equal(original.PhysicalUnit, restored.PhysicalUnit);

            // 坐标系与标定是嵌套对象——最容易"序列化看起来成功、读回来是 null"。
            WorkpieceCoordinateSystem? originalSystem = original.CoordinateSystem;
            CameraCalibration? originalCalibration = original.Calibration;
            WorkpieceCoordinateSystem? restoredSystem = restored.CoordinateSystem;
            CameraCalibration? restoredCalibration = restored.Calibration;

            Assert.NotNull(originalSystem);
            Assert.NotNull(originalCalibration);
            Assert.NotNull(restoredSystem);
            Assert.NotNull(restoredCalibration);

            Assert.Equal(originalSystem.Origin, restoredSystem.Origin);
            Assert.Equal(originalSystem.AxisX, restoredSystem.AxisX);
            Assert.Equal(originalSystem.Name, restoredSystem.Name);

            Assert.Equal(originalCalibration.K1, restoredCalibration.K1);
            Assert.Equal(originalCalibration.K2, restoredCalibration.K2);
            Assert.Equal(originalCalibration.NormalizationRadius, restoredCalibration.NormalizationRadius);
            Assert.Equal(originalCalibration.CalibrationId, restoredCalibration.CalibrationId);
            Assert.Equal(
                originalCalibration.MeasurementUncertaintyPixels,
                restoredCalibration.MeasurementUncertaintyPixels);

            Assert.Equal(2, restored.DatumPoints.Count);
            Assert.Equal("孔2", restored.DatumPoints[1].Name);
            Assert.Equal(100, restored.DatumPoints[1].X);
            Assert.Equal(20, restored.DatumPoints[1].Y);

            Assert.Equal(2, restored.Items.Count);
            Assert.Equal("circle-measure#0", restored.Items[0].Key);
            Assert.Equal("内径 A", restored.Items[0].DisplayName);
            Assert.Equal("直径", restored.Items[0].Quantity);

            MeasurementTolerance? restoredTolerance = restored.Items[0].Tolerance;
            Assert.NotNull(restoredTolerance);
            Assert.Equal(20.0, restoredTolerance.Nominal);
            Assert.Equal(0.05, restoredTolerance.TolerancePlus);
            Assert.Equal(0.05, restoredTolerance.ToleranceMinus);
            // IsEnabled=false 必须活着回来，否则被停用的项会重新参与判定。
            Assert.False(restored.Items[1].IsEnabled);
            Assert.Null(restored.Items[1].Tolerance);
        }

        [Fact]
        public void DerivedPropertiesAreNotSerialized()
        {
            string json = JsonSerializer.Serialize(
                CreateSample(),
                ImageViewerCoreJsonSerializationContext.Default.MeasurementRecipeDocument);

            // [JsonIgnore] 的派生属性若被写进文件，读回时就成了"可被外部伪造"的字段。
            Assert.DoesNotContain("EnabledItemCount", json, StringComparison.Ordinal);
            Assert.DoesNotContain("CanLocalize", json, StringComparison.Ordinal);
            Assert.DoesNotContain("HasValidScale", json, StringComparison.Ordinal);
        }

        [Fact]
        public void EnabledItemCountIgnoresDisabledItems()
        {
            MeasurementRecipeDocument recipe = CreateSample();

            Assert.Equal(2, recipe.Items.Count);
            Assert.Equal(1, recipe.EnabledItemCount);
        }

        [Fact]
        public void CanLocalizeRequiresTwoDistinctDatumPoints()
        {
            var recipe = new MeasurementRecipeDocument();

            // 没有基准点：连原点都定不了。
            Assert.False(recipe.CanLocalize);

            // 只有一个基准点：能定原点，定不了基准轴 ⇒ 不构成完整定位。
            recipe.DatumPoints.Add(new RecipeDatumPoint { Name = "孔1", X = 10, Y = 10 });
            Assert.False(recipe.CanLocalize);

            // 两个点但完全重合：仍然没有基准轴方向 ⇒ 与"只有一个点"等价。
            recipe.DatumPoints.Add(new RecipeDatumPoint { Name = "孔2", X = 10, Y = 10 });
            Assert.False(recipe.CanLocalize);

            // 两点分开（哪怕只在一个分量上）才算可定位。
            recipe.DatumPoints[1].Y = 10.5;
            Assert.True(recipe.CanLocalize);
        }

        [Fact]
        public void HasValidScaleRejectsNonPositiveAndNonFinitePixelSize()
        {
            var recipe = new MeasurementRecipeDocument { PixelSize = 0.01 };
            Assert.True(recipe.HasValidScale);

            recipe.PixelSize = 0;
            Assert.False(recipe.HasValidScale);

            recipe.PixelSize = -0.01;
            Assert.False(recipe.HasValidScale);

            recipe.PixelSize = double.NaN;
            Assert.False(recipe.HasValidScale);

            recipe.PixelSize = double.PositiveInfinity;
            Assert.False(recipe.HasValidScale);
        }

        [Fact]
        public void DefaultDocumentReportsVersionZeroSoItIsRecognisedAsLegacy()
        {
            // 与 ImageViewerSessionDocument 同约定：缺失/非正版本按早期文件宽容处理，
            // 因此 default 构造出来的配方必须能被识别为"没有版本号"，而不是冒充当前版本。
            var recipe = new MeasurementRecipeDocument();

            Assert.Equal(0, recipe.Version);
            Assert.NotEqual(MeasurementRecipeDocument.CurrentVersion, recipe.Version);
        }

        [Fact]
        public void LocalizedRecipeSurvivesRoundTripAndStillMapsRoiToImage()
        {
            // 端到端语义：配方存名义基准，测量时用图像上的实际基准重新定位。
            MeasurementRecipeDocument recipe = CreateSample();
            string json = JsonSerializer.Serialize(
                recipe,
                ImageViewerCoreJsonSerializationContext.Default.MeasurementRecipeDocument);
            MeasurementRecipeDocument restored = JsonSerializer.Deserialize(
                json,
                ImageViewerCoreJsonSerializationContext.Default.MeasurementRecipeDocument)!;

            Assert.Equal(2, restored.DatumPoints.Count);

            // 工件在图上平移+旋转 90°：名义基准 (0,0)-(100,20) 对应图像 (500,300)-(480,400)。
            WorkpieceCoordinateSystem localized = WorkpieceCoordinateSystem.Localize(
                new PointD(500, 300),
                new PointD(480, 400),
                new PointD(restored.DatumPoints[0].X, restored.DatumPoints[0].Y),
                new PointD(restored.DatumPoints[1].X, restored.DatumPoints[1].Y));

            Assert.True(localized.IsDefined);

            // 图像上的基准点必须映回名义基准（往返闭合），否则所有 ROI 都会偏。
            PointD backP1 = localized.ToWorkpiece(new PointD(500, 300));
            Assert.Equal(restored.DatumPoints[0].X, backP1.X, 6);
            Assert.Equal(restored.DatumPoints[0].Y, backP1.Y, 6);

            PointD backP2 = localized.ToWorkpiece(new PointD(480, 400));
            Assert.Equal(restored.DatumPoints[1].X, backP2.X, 6);
            Assert.Equal(restored.DatumPoints[1].Y, backP2.Y, 6);
        }
    }
}
