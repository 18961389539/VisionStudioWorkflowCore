using System;
using ImageViewer.Core.Geometry;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// 工件坐标系守卫测试。
    /// Chinese: 重点不是"旋转公式写得对不对"，而是三件会静默毁掉测量的退化行为：
    /// ① 基准轴退化时不能悄悄返回单位变换；② 变换必须真的是刚体（长度不变）；
    /// ③ 往返必须闭合到浮点精度内，否则 ROI 会在反复应用后漂移。
    /// </summary>
    [Trait("Category", "Unit")]
    public sealed class WorkpieceCoordinateSystemTests
    {
        private const double Tolerance = 1e-9;

        [Fact]
        public void DatumOriginMapsToZeroAndAxisMapsToPositiveX()
        {
            var origin = new PointD(100, 50);
            var axis = new VectorD(0, 20); // 图像 +Y 方向作为基准轴

            RigidTransform2D transform = RigidTransform2D.FromDatum(origin, axis);

            PointD mappedOrigin = transform.Transform(origin);
            Assert.Equal(0, mappedOrigin.X, 9);
            Assert.Equal(0, mappedOrigin.Y, 9);

            PointD mappedAxis = transform.Transform(new PointD(origin.X + axis.X, origin.Y + axis.Y));
            // 基准轴应落在 +X 上，长度为轴长，Y 分量为 0。
            Assert.Equal(20, mappedAxis.X, 9);
            Assert.Equal(0, mappedAxis.Y, 9);
        }

        [Fact]
        public void TransformIsRigidSoDistancesArePreserved()
        {
            RigidTransform2D transform = RigidTransform2D.FromDatum(
                new PointD(37, -12),
                new VectorD(3, 4));

            var a = new PointD(10, 10);
            var b = new PointD(42, -7);
            double before = a.DistanceTo(b);
            double after = transform.Transform(a).DistanceTo(transform.Transform(b));

            // 若这里引入缩放，"同一个工件在不同焦距下尺寸不同"，尺寸测量就失去可比性。
            Assert.Equal(before, after, 9);
        }

        [Fact]
        public void InverseRoundTripsToOriginalPoint()
        {
            RigidTransform2D transform = RigidTransform2D.FromDatum(
                new PointD(-20, 15),
                new VectorD(-7, 3));

            var original = new PointD(123.456, -78.9);
            PointD roundTripped = transform.Inverse(transform.Transform(original));

            Assert.Equal(original.X, roundTripped.X, 9);
            Assert.Equal(original.Y, roundTripped.Y, 9);
        }

        [Fact]
        public void DegenerateDatumAxisIsRejectedInsteadOfSilentlyIdentity()
        {
            // 基准两点重合 ⇒ 无法确定基准轴。静默返回单位变换会让所有测量错位却看起来正常。
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RigidTransform2D.FromDatum(new PointD(1, 2), new VectorD(0, 0)));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RigidTransform2D.FromDatumPair(
                    new PointD(5, 5), new PointD(5, 5),
                    new PointD(0, 0), new PointD(10, 0)));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RigidTransform2D.FromDatumPair(
                    new PointD(0, 0), new PointD(10, 0),
                    new PointD(7, 7), new PointD(7, 7)));
        }

        [Fact]
        public void NonFiniteDatumIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RigidTransform2D.FromDatum(new PointD(double.NaN, 0), new VectorD(1, 0)));
        }

        [Fact]
        public void UndefinedCoordinateSystemDegradesToIdentityRatherThanThrowing()
        {
            // 未定义坐标系不应中断整个测量流程；"工件坐标 = 图像坐标"是唯一合理的退化。
            var undefined = new WorkpieceCoordinateSystem { Origin = new PointD(0, 0), AxisX = new VectorD(0, 0) };

            Assert.False(undefined.IsDefined);
            Assert.True(undefined.ImageToWorkpieceTransform.IsIdentity);

            var point = new PointD(11, 22);
            Assert.Equal(point, undefined.ToWorkpiece(point));
            Assert.Equal(point, undefined.ToImage(point));
        }

        [Fact]
        public void LocalizeRecoversTranslationAndRotationFromImageDatums()
        {
            // 名义基准：原点 (0,0)，+X 轴指向 (100,0)。
            var nominalP1 = new PointD(0, 0);
            var nominalP2 = new PointD(100, 0);

            // 图像上工件平移到 (500,300) 并旋转 90°（基准轴指向图像 +Y）。
            var imageP1 = new PointD(500, 300);
            var imageP2 = new PointD(500, 400);

            WorkpieceCoordinateSystem system = WorkpieceCoordinateSystem.Localize(
                imageP1, imageP2, nominalP1, nominalP2, name: "上表面基准");

            Assert.True(system.IsDefined);
            // 图像上的原点应映射到工件 (0,0)。
            PointD originInWorkpiece = system.ToWorkpiece(imageP1);
            Assert.Equal(0, originInWorkpiece.X, 6);
            Assert.Equal(0, originInWorkpiece.Y, 6);

            // 工件上 (100,0) 应映回图像 (500,400)。
            PointD axisInImage = system.ToImage(new PointD(100, 0));
            Assert.Equal(500, axisInImage.X, 6);
            Assert.Equal(400, axisInImage.Y, 6);

            // 基准轴相对图像 +X 旋转了 90°。
            Assert.Equal(90, Math.Abs(system.DatumAngleDegrees), 6);
        }

        [Fact]
        public void LocalizedSystemMovesRoisWithTheWorkpiece()
        {
            var nominalP1 = new PointD(0, 0);
            var nominalP2 = new PointD(100, 0);
            // 同一个工件在两"张图"里位置不同：这正是批量分析复用 ROI 时遇到的问题。
            WorkpieceCoordinateSystem first = WorkpieceCoordinateSystem.Localize(
                new PointD(0, 0), new PointD(100, 0), nominalP1, nominalP2);
            WorkpieceCoordinateSystem second = WorkpieceCoordinateSystem.Localize(
                new PointD(400, 150), new PointD(400, 250), nominalP1, nominalP2);

            // 配方里的一个工件坐标 ROI 中心。
            var workpieceRoi = new PointD(50, 10);

            PointD onFirst = first.ToImage(workpieceRoi);
            PointD onSecond = second.ToImage(workpieceRoi);

            // 两次定位得到的图像位置必须不同，但它们在工件坐标下是同一个点。
            Assert.NotEqual(onFirst.X, onSecond.X, 3);
            Assert.Equal(workpieceRoi.X, first.ToWorkpiece(onFirst).X, 6);
            Assert.Equal(workpieceRoi.Y, second.ToWorkpiece(onSecond).Y, 6);
        }

        [Fact]
        public void TransformAngleAddsAConstantRotation()
        {
            RigidTransform2D transform = RigidTransform2D.FromDatum(new PointD(0, 0), new VectorD(1, 1));

            // 角度差在刚体变换下不变——这正是平行度/垂直度不需要跟着坐标系重算的原因。
            Assert.Equal(30 + transform.RotationDegrees, transform.TransformAngleDegrees(30), 9);
        }

        [Fact]
        public void TransformDirectionDoesNotTranslate()
        {
            RigidTransform2D transform = RigidTransform2D.FromDatum(new PointD(999, -999), new VectorD(0, 5));

            var direction = new VectorD(1, 0);
            VectorD mapped = transform.TransformDirection(direction);
            VectorD roundTripped = transform.InverseDirection(mapped);

            // 方向向量是"自由向量"，不能把平移量混进去。
            Assert.Equal(1, roundTripped.X, 9);
            Assert.Equal(0, roundTripped.Y, 9);

            // 纯旋转必须保长度（旋转矩阵正交），否则角度类测量会随定位结果变化。
            Assert.Equal(1, Math.Sqrt(mapped.X * mapped.X + mapped.Y * mapped.Y), 9);
        }
    }
}
