using System;
using System.Globalization;
using ImageViewer.Models;

namespace ImageViewer.Core.Geometry
{
    /// <summary>
    /// 由两个基准点定义的二维刚体变换（平移 + 旋转，不含缩放）。
    /// Chinese: 这是"工件坐标系"的数学内核。刻意**只做刚体变换**——图像的物理尺度由 PixelSize/标定
    /// 负责，若这里再引入缩放，同一个工件在不同焦距下会得到不同的"工件尺寸"，尺寸测量就失去可比性。
    /// English: A 2D rigid transform (rotation + translation, no scale) derived from two datum points.
    /// Deliberately rigid-only: physical scale belongs to PixelSize/calibration, and letting this type scale
    /// would make the same workpiece measure differently at different focus distances.
    /// </summary>
    public readonly record struct RigidTransform2D(double Cos, double Sin, double TranslationX, double TranslationY)
    {
        /// <summary>单位变换（恒等）。</summary>
        public static RigidTransform2D Identity { get; } = new(1, 0, 0, 0);

        /// <summary>旋转角（弧度）。</summary>
        public double RotationRadians => Math.Atan2(Sin, Cos);

        /// <summary>旋转角（度）。</summary>
        public double RotationDegrees => RotationRadians * 180.0 / Math.PI;

        /// <summary>是否为单位变换。</summary>
        public bool IsIdentity =>
            Math.Abs(Cos - 1) < 1e-12 && Math.Abs(Sin) < 1e-12 &&
            Math.Abs(TranslationX) < 1e-12 && Math.Abs(TranslationY) < 1e-12;

        /// <summary>
        /// 由"基准原点 + 基准轴方向"构造变换：把图像坐标映射到工件坐标。
        /// Chinese: 结果满足 <c>origin → (0,0)</c>、<c>origin + direction → (L,0)</c>，即
        /// 原点落在工件原点、基准轴对齐工件 +X。
        /// </summary>
        /// <param name="origin">基准原点（图像坐标）。</param>
        /// <param name="axisDirection">基准轴方向（图像坐标下的方向向量，长度必须 &gt; 0）。</param>
        public static RigidTransform2D FromDatum(PointD origin, VectorD axisDirection)
        {
            double length = Math.Sqrt(axisDirection.X * axisDirection.X + axisDirection.Y * axisDirection.Y);
            if (!double.IsFinite(length) || length <= 1e-12)
            {
                throw new ArgumentOutOfRangeException(nameof(axisDirection), "基准轴方向向量长度必须大于零。");
            }

            if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(origin), "基准原点必须是有限坐标。");
            }

            // 把基准轴旋转到 +X：旋转角 = -atan2(dy, dx)。
            double cos = axisDirection.X / length;
            double sin = -axisDirection.Y / length;

            // 平移量 = -R * origin（使 origin 映射到原点）。
            double translationX = -(cos * origin.X - sin * origin.Y);
            double translationY = -(sin * origin.X + cos * origin.Y);
            return new RigidTransform2D(cos, sin, translationX, translationY);
        }

        /// <summary>
        /// 由"图像上的基准点对"和"工件上的名义基准点对"求解变换（自动定位）。
        /// Chinese: 这是自动定位的入口——工件平移/旋转后，只要还能找到这两个基准特征，
        /// 就能把配方里的工件坐标 ROI 投回图像坐标。退化情形（两点重合）必须显式抛错而不是
        /// 悄悄返回单位变换，否则所有测量都会错位却看起来"很正常"。
        /// </summary>
        /// <param name="imageP1">图像上的第一个基准点。</param>
        /// <param name="imageP2">图像上的第二个基准点。</param>
        /// <param name="nominalP1">工件上的第一个名义基准点。</param>
        /// <param name="nominalP2">工件上的第二个名义基准点。</param>
        public static RigidTransform2D FromDatumPair(
            PointD imageP1,
            PointD imageP2,
            PointD nominalP1,
            PointD nominalP2)
        {
            double imageDx = imageP2.X - imageP1.X;
            double imageDy = imageP2.Y - imageP1.Y;
            double nominalDx = nominalP2.X - nominalP1.X;
            double nominalDy = nominalP2.Y - nominalP1.Y;

            double imageLength = Math.Sqrt(imageDx * imageDx + imageDy * imageDy);
            double nominalLength = Math.Sqrt(nominalDx * nominalDx + nominalDy * nominalDy);
            if (!double.IsFinite(imageLength) || imageLength <= 1e-12)
            {
                throw new ArgumentOutOfRangeException(nameof(imageP2), "图像基准点重合，无法确定基准轴。");
            }

            if (!double.IsFinite(nominalLength) || nominalLength <= 1e-12)
            {
                throw new ArgumentOutOfRangeException(nameof(nominalP2), "名义基准点重合，无法确定基准轴。");
            }

            // 图像基准轴在工件坐标下的方向 = 命中轴与名义轴的夹角。
            // 构造 image→workpiece 的变换：先把图像基准轴转到名义基准轴方向，再把 imageP1 映射到 nominalP1。
            double imageAngle = Math.Atan2(imageDy, imageDx);
            double nominalAngle = Math.Atan2(nominalDy, nominalDx);
            double rotation = nominalAngle - imageAngle;
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);

            // p_workpiece = R * (p_image - imageP1) + nominalP1
            double translationX = nominalP1.X - (cos * imageP1.X - sin * imageP1.Y);
            double translationY = nominalP1.Y - (sin * imageP1.X + cos * imageP1.Y);
            return new RigidTransform2D(cos, sin, translationX, translationY);
        }

        /// <summary>把图像坐标映射到工件坐标。</summary>
        public PointD Transform(PointD point) => new(
            Cos * point.X - Sin * point.Y + TranslationX,
            Sin * point.X + Cos * point.Y + TranslationY);

        /// <summary>把工件坐标映射回图像坐标（逆变换，刚体变换的逆 = 转置 + 反向平移）。</summary>
        /// <remarks>
        /// Chinese: 逆变换是 <c>R^T * (q - t)</c>，注意平移量要在**旋转之前**整向量减掉；
        /// 曾经写成"按分量各自减 TranslationX/Y"（等于 <c>R^T * q - t</c>），只在平移为 0 时才正确，
        /// 结果是坐标系一定位到非原点位置，往返就不再闭合、ROI 会逐帧漂移。</remarks>
        public PointD Inverse(PointD point)
        {
            double dx = point.X - TranslationX;
            double dy = point.Y - TranslationY;
            return new PointD(Cos * dx + Sin * dy, -Sin * dx + Cos * dy);
        }

        /// <summary>把方向向量（只旋转，不平移）映射到工件坐标。</summary>
        public VectorD TransformDirection(VectorD direction) => new(
            Cos * direction.X - Sin * direction.Y,
            Sin * direction.X + Cos * direction.Y);

        /// <summary>把方向向量映射回图像坐标。</summary>
        public VectorD InverseDirection(VectorD direction) => new(
            Cos * direction.X + Sin * direction.Y,
            -Sin * direction.X + Cos * direction.Y);

        /// <summary>
        /// 求逆变换。
        /// Chinese: 刚体变换的逆就是"转置旋转 + 反向平移"，但顺序不能错——必须
        /// <c>R^T * (q - t)</c>，即先整向量减掉平移再旋转。曾经把平移写进旋转矩阵的各个分量里，
        /// 只在 t=0 时正确；一旦工件不在图像原点，往返就不闭合、ROI 会随每次应用漂移。
        /// 刻意不叫 <c>Inverse()</c>：同名的 <see cref="Inverse(PointD)"/> 是"对某个点做逆映射"，
        /// 两者混用极易写错方向。
        /// </summary>
        public RigidTransform2D Inverted()
        {
            double cos = Cos;
            double sin = -Sin;
            double translationX = -(cos * TranslationX - sin * TranslationY);
            double translationY = -(sin * TranslationX + cos * TranslationY);
            return new RigidTransform2D(cos, sin, translationX, translationY);
        }

        /// <summary>
        /// 变换角度（度）。
        /// Chinese: 刚体变换只加一个常量旋转角——因此"平行度/垂直度"这类角度差在工件坐标系下不变，
        /// 这也是为什么它们不需要重新计算。
        /// </summary>
        public double TransformAngleDegrees(double angleDegrees) => angleDegrees + RotationDegrees;
    }

    /// <summary>
    /// 工件坐标系：原点 + 基准轴。
    /// Chinese: 让测量区域随工件平移/旋转的核心抽象。它同时保存"名义定义"（配方里写的）与
    /// "当前定位结果"（本张图上算出来的），两者的差异就是工件的装夹偏差——这本身也是有用的生产信息。
    /// English: Origin plus datum axis. Stores both the nominal definition (from the recipe) and the current
    /// localization (solved from this image); their difference is the fixturing offset, which is itself
    /// useful production data.
    /// </summary>
    public sealed record WorkpieceCoordinateSystem
    {
        /// <summary>工件原点在图像坐标下的位置。</summary>
        public PointD Origin { get; init; }

        /// <summary>工件 +X 轴在图像坐标下的方向（无需归一化）。</summary>
        public VectorD AxisX { get; init; } = new(1, 0);

        /// <summary>坐标系名称（例如 "上表面基准" / "孔系基准"）。</summary>
        public string? Name { get; init; }

        /// <summary>
        /// 由基准点对显式求得的 image → workpiece 变换。
        /// Chinese: <see cref="Localize"/> 会填入它。之所以要单独存一份而不是每次由
        /// <see cref="Origin"/>/<see cref="AxisX"/> 重算：<see cref="RigidTransform2D.FromDatum"/>
        /// 会把基准轴**归一化**，于是"名义基准点到原点的距离"被丢掉；当名义基准点不在单位长度上时
        /// （例如 (0,0)-(100,20)），重算出来的变换与 <see cref="RigidTransform2D.FromDatumPair"/>
        /// 的结果不同——差值正好就是名义基准点长度。存下来才能保证"图像基准点映回名义基准点"精确闭合。
        /// 手工构造（只给 Origin/AxisX）时为 null，此时退化为单位轴语义。
        /// </summary>
        public RigidTransform2D? LocalizedImageToWorkpiece { get; init; }

        /// <summary>
        /// 是否已定义了一个可用的坐标系。
        /// Chinese: 轴长为 0 或非有限值视为未定义——此时所有映射必须退化为恒等，
        /// 而不是抛异常中断整个测量流程。
        /// </summary>
        public bool IsDefined =>
            double.IsFinite(Origin.X) && double.IsFinite(Origin.Y) &&
            double.IsFinite(AxisX.X) && double.IsFinite(AxisX.Y) &&
            Math.Sqrt(AxisX.X * AxisX.X + AxisX.Y * AxisX.Y) > 1e-12;

        /// <summary>基准轴相对图像 +X 的夹角（度）。</summary>
        public double DatumAngleDegrees =>
            IsDefined ? Math.Atan2(AxisX.Y, AxisX.X) * 180.0 / Math.PI : 0;

        /// <summary>
        /// 图像坐标 → 工件坐标的变换。
        /// Chinese: 未定义坐标系时返回单位变换（"工件坐标 = 图像坐标"），使上层无需到处判空。
        /// 若坐标系带显式定位结果（来自基准点对），优先用它，避免归一化造成的精度/语义偏差。
        /// </summary>
        public RigidTransform2D ImageToWorkpieceTransform
        {
            get
            {
                if (!IsDefined)
                {
                    return RigidTransform2D.Identity;
                }

                return LocalizedImageToWorkpiece ?? RigidTransform2D.FromDatum(Origin, AxisX);
            }
        }

        /// <summary>工件坐标 → 图像坐标的变换。</summary>
        /// <remarks>Chinese: 未定义坐标系时正向是单位变换，其逆同样是单位变换，退化路径自然成立。</remarks>
        public RigidTransform2D WorkpieceToImageTransform => ImageToWorkpieceTransform.Inverted();

        /// <summary>把图像点映射到工件坐标。</summary>
        public PointD ToWorkpiece(PointD imagePoint) => ImageToWorkpieceTransform.Transform(imagePoint);

        /// <summary>把工件坐标点映射回图像坐标。</summary>
        public PointD ToImage(PointD workpiecePoint) => WorkpieceToImageTransform.Transform(workpiecePoint);

        /// <summary>
        /// 由"图像上的两个基准点 + 工件上的两个名义基准点"重新定位。
        /// Chinese: 这就是自动定位：配方里的名义基准点是固定的，每张图上重新找到的基准点是可变的，
        /// 两者的变换把工件坐标下的 ROI 投到当前图上。
        /// </summary>
        public static WorkpieceCoordinateSystem Localize(
            PointD imageP1,
            PointD imageP2,
            PointD nominalP1,
            PointD nominalP2,
            string? name = null)
        {
            // FromDatumPair 的方向是 image → workpiece（把图像基准轴转到名义轴、把 imageP1 映到 nominalP1）。
            // 坐标系要存的是"原点在图像的哪里、基准轴在图像上朝哪"，因此这里必须用它的**逆**把
            // 名义基准点映回图像，而不是直接正向应用——直接正向会得到一组镜像/偏移的原点和轴。
            RigidTransform2D imageToWorkpiece = RigidTransform2D.FromDatumPair(imageP1, imageP2, nominalP1, nominalP2);
            RigidTransform2D workpieceToImage = imageToWorkpiece.Inverted();
            PointD origin = workpieceToImage.Transform(nominalP1);
            VectorD axis = workpieceToImage.TransformDirection(new VectorD(nominalP2.X - nominalP1.X, nominalP2.Y - nominalP1.Y));
            return new WorkpieceCoordinateSystem
            {
                Origin = origin,
                AxisX = axis,
                Name = name,
                // 显式带上精确变换：Origin/AxisX 只用于描述（显示角度、做诊断），
                // 真正参与映射的是这一份，避免 FromDatum 归一化丢掉名义基准点长度。
                LocalizedImageToWorkpiece = imageToWorkpiece
            };
        }

        /// <inheritdoc />
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{Name ?? "工件坐标系"}: O={Origin}, X={DatumAngleDegrees:F3}°");
    }
}
