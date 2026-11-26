using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// 相机 Brown-Conrady 畸变标定参数。
    /// Chinese: 保留 K1/K2 的旧格式兼容性，同时支持 K3 和切向畸变 P1/P2。
    /// 坐标以 NormalizationRadius 归一化，完整模型可对像点做迭代去畸变；旧的局部比例 API
    /// 仍然保留，便于历史会话和插件继续工作。
    /// English: Brown-Conrady camera calibration. K1/K2 remain backward compatible while K3 and
    /// tangential P1/P2 enable full point undistortion.
    /// </summary>
    public sealed class CameraCalibration
    {
        private const double MinScaleDenominator = 0.2;
        private const int MaximumUndistortIterations = 20;
        private const double UndistortResidualTolerancePixels = 1e-6;

        /// <summary>
        /// 主点 X（像素）。Chinese: 默认取图像中心。English: Principal PointD X in pixels.
        /// </summary>
        public double PrincipalX { get; set; }

        /// <summary>
        /// 主点 Y（像素）。Chinese: 默认取图像中心。English: Principal PointD Y in pixels.
        /// </summary>
        public double PrincipalY { get; set; }

        /// <summary>
        /// 一阶径向畸变系数（基于归一化半径）。
        /// English: First-order radial distortion coefficient (normalized radius).
        /// </summary>
        public double K1 { get; set; }

        /// <summary>
        /// 二阶径向畸变系数（基于归一化半径）。
        /// English: Second-order radial distortion coefficient (normalized radius).
        /// </summary>
        public double K2 { get; set; }

        /// <summary>三阶径向畸变系数。</summary>
        public double K3 { get; set; }

        /// <summary>切向畸变系数 P1。</summary>
        public double TangentialP1 { get; set; }

        /// <summary>切向畸变系数 P2。</summary>
        public double TangentialP2 { get; set; }

        /// <summary>
        /// 归一化参考半径（像素）。Chinese: 小于等于 0 时视为未配置，不做畸变校正。
        /// English: Normalization reference radius in pixels; &lt;= 0 disables correction.
        /// </summary>
        public double NormalizationRadius { get; set; } = 1.0;

        /// <summary>标定重投影 RMS（像素），用于判断标定是否达到当前测量精度要求。</summary>
        public double ReprojectionErrorRms { get; set; }

        /// <summary>标定引入的估计测量不确定度（像素，1 sigma）。</summary>
        public double MeasurementUncertaintyPixels { get; set; }

        /// <summary>外部标定证书或标定记录的标识。</summary>
        public string? CalibrationId { get; set; }

        /// <summary>标定完成时间。</summary>
        public DateTimeOffset? CalibratedAtUtc { get; set; }

        /// <summary>Copies all calibration values for a detached measurement or persistence snapshot.</summary>
        public CameraCalibration Clone() => (CameraCalibration)MemberwiseClone();

        /// <summary>
        /// 是否配置了畸变系数。
        /// English: Whether any distortion coefficient is configured.
        /// </summary>
        public bool IsEnabled =>
            Math.Abs(K1) > 1e-12 || Math.Abs(K2) > 1e-12 || Math.Abs(K3) > 1e-12 ||
            Math.Abs(TangentialP1) > 1e-12 || Math.Abs(TangentialP2) > 1e-12;

        /// <summary>
        /// 标定参数是否适合参与高精度计算。
        /// </summary>
        public bool IsValid =>
            double.IsFinite(PrincipalX) && double.IsFinite(PrincipalY) &&
            double.IsFinite(NormalizationRadius) && NormalizationRadius > 0 &&
            double.IsFinite(K1) && double.IsFinite(K2) && double.IsFinite(K3) &&
            double.IsFinite(TangentialP1) && double.IsFinite(TangentialP2) &&
            double.IsFinite(ReprojectionErrorRms) && ReprojectionErrorRms >= 0 &&
            double.IsFinite(MeasurementUncertaintyPixels) && MeasurementUncertaintyPixels >= 0;

        /// <summary>
        /// 完整模型是否包含旧 K1/K2 之外的项。
        /// </summary>
        public bool HasExtendedDistortionModel =>
            Math.Abs(K3) > 1e-12 || Math.Abs(TangentialP1) > 1e-12 || Math.Abs(TangentialP2) > 1e-12;

        /// <summary>
        /// 归一化半径平方 r²。
        /// English: Squared normalized radius at an image PointD.
        /// </summary>
        public double NormalizedRadiusSquared(PointD imagePoint)
        {
            if (NormalizationRadius <= 0)
            {
                return 0;
            }

            double dx = imagePoint.X - PrincipalX;
            double dy = imagePoint.Y - PrincipalY;
            return (dx * dx + dy * dy) / (NormalizationRadius * NormalizationRadius);
        }

        /// <summary>
        /// 去畸变缩放因子：将局部畸变像素长度换算为理想像素长度的乘数。
        /// Chinese: 取畸变放大倍率的倒数，并对极端参数做截断保护。
        /// English: Factor converting local distorted pixels to ideal pixels.
        /// </summary>
        public double UndistortScaleFactor(PointD imagePoint)
        {
            if (!IsEnabled || !IsValid)
            {
                return 1.0;
            }

            double r2 = NormalizedRadiusSquared(imagePoint);
            double denominator = 1.0 + K1 * r2 + K2 * r2 * r2 + K3 * r2 * r2 * r2;
            if (denominator < MinScaleDenominator)
            {
                denominator = MinScaleDenominator;
            }

            return 1.0 / denominator;
        }

        /// <summary>
        /// 对像素长度应用畸变校正（以线段中点为校正参考点）。
        /// Chinese: 返回校正后的等效像素长度，后端换算物理长度时再乘像素尺寸 PixelSize。
        /// English: Corrects a pixel-length measured at the given midpoint.
        /// </summary>
        public double CorrectPixelLength(double pixelLength, PointD midPoint)
        {
            if (!HasExtendedDistortionModel)
            {
                return pixelLength * UndistortScaleFactor(midPoint);
            }

            return pixelLength * LocalLengthScaleFactor(midPoint);
        }

        /// <summary>
        /// 对一条线段的两个端点分别去畸变后计算长度。
        /// </summary>
        public double CorrectPixelLength(PointD start, PointD end)
        {
            PointD correctedStart = UndistortPoint(start);
            PointD correctedEnd = UndistortPoint(end);
            double dx = correctedEnd.X - correctedStart.X;
            double dy = correctedEnd.Y - correctedStart.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 将理想像点映射到畸变像点（Brown-Conrady 正向模型）。
        /// </summary>
        public PointD DistortPoint(PointD idealPoint)
        {
            if (!IsEnabled || !IsValid)
            {
                return idealPoint;
            }

            (double x, double y) = ToNormalized(idealPoint);
            (double xd, double yd) = ApplyDistortion(x, y);
            return FromNormalized(xd, yd);
        }

        /// <summary>
        /// 通过牛顿迭代将畸变像点还原为理想像点。
        /// English: Inverts the distortion model with Newton iterations.
        /// </summary>
        public PointD UndistortPoint(PointD distortedPoint) => Undistort(distortedPoint).Point;

        /// <summary>
        /// 反解畸变模型并报告收敛状态。
        /// Chinese: 以解析 Jacobian 的牛顿迭代替代固定点迭代，残差按像素与阈值比较；
        /// Jacobian 退化时回退到固定点步长，参数导致模型不可逆时返回未收敛而非静默给出错点。
        /// English: Newton inversion with an analytic Jacobian and a pixel residual threshold.
        /// A degenerate Jacobian falls back to the fixed-point step; a non-invertible model is
        /// reported as not converged instead of silently returning a wrong point.
        /// </summary>
        public UndistortResult Undistort(PointD distortedPoint)
        {
            if (!IsEnabled || !IsValid)
            {
                return new UndistortResult(distortedPoint, Converged: true, Iterations: 0, ResidualPixels: 0);
            }

            if (!double.IsFinite(distortedPoint.X) || !double.IsFinite(distortedPoint.Y))
            {
                return new UndistortResult(distortedPoint, Converged: false, Iterations: 0, ResidualPixels: double.PositiveInfinity);
            }

            (double targetX, double targetY) = ToNormalized(distortedPoint);
            double x = targetX;
            double y = targetY;
            for (int iteration = 0; iteration < MaximumUndistortIterations; iteration++)
            {
                (double projectedX, double projectedY) = ApplyDistortion(x, y);
                double errorX = targetX - projectedX;
                double errorY = targetY - projectedY;
                double residualPixels = Math.Sqrt((errorX * errorX) + (errorY * errorY)) * NormalizationRadius;
                if (!double.IsFinite(residualPixels))
                {
                    break;
                }

                if (residualPixels <= UndistortResidualTolerancePixels)
                {
                    return new UndistortResult(FromNormalized(x, y), Converged: true, iteration, residualPixels);
                }

                (double dXdx, double dXdy, double dYdx, double dYdy) = DistortionJacobian(x, y);
                double determinant = (dXdx * dYdy) - (dXdy * dYdx);
                double stepX;
                double stepY;
                if (Math.Abs(determinant) > 1e-12 && double.IsFinite(determinant))
                {
                    stepX = ((dYdy * errorX) - (dXdy * errorY)) / determinant;
                    stepY = ((dXdx * errorY) - (dYdx * errorX)) / determinant;
                }
                else
                {
                    stepX = errorX;
                    stepY = errorY;
                }

                double nextX = x + stepX;
                double nextY = y + stepY;
                if (!double.IsFinite(nextX) || !double.IsFinite(nextY))
                {
                    break;
                }

                x = nextX;
                y = nextY;
            }

            (double finalX, double finalY) = ApplyDistortion(x, y);
            double finalErrorX = targetX - finalX;
            double finalErrorY = targetY - finalY;
            double finalResidual = Math.Sqrt((finalErrorX * finalErrorX) + (finalErrorY * finalErrorY)) * NormalizationRadius;
            if (!double.IsFinite(finalResidual))
            {
                finalResidual = double.PositiveInfinity;
            }

            return new UndistortResult(FromNormalized(x, y), Converged: false, MaximumUndistortIterations, finalResidual);
        }

        /// <summary>
        /// 点的 1σ 不确定度：标定记录的不确定度，反解未收敛时取不小于未收敛残差的值。
        /// English: 1-sigma point uncertainty: the recorded calibration uncertainty, inflated to
        /// the undistortion residual when the inverse model does not converge.
        /// </summary>
        public double GetPointUncertaintyPixels(PointD distortedPoint)
        {
            if (!IsEnabled || !IsValid)
            {
                return MeasurementUncertaintyPixels;
            }

            UndistortResult result = Undistort(distortedPoint);
            return result.Converged
                ? MeasurementUncertaintyPixels
                : Math.Max(MeasurementUncertaintyPixels, result.ResidualPixels);
        }

        /// <summary>
        /// 对扩展畸变模型用局部数值 Jacobian 估计无方向长度比例。
        /// </summary>
        public double LocalLengthScaleFactor(PointD imagePoint)
        {
            if (!HasExtendedDistortionModel)
            {
                return UndistortScaleFactor(imagePoint);
            }

            const double epsilon = 0.5;
            PointD left = UndistortPoint(new PointD(imagePoint.X - epsilon, imagePoint.Y));
            PointD right = UndistortPoint(new PointD(imagePoint.X + epsilon, imagePoint.Y));
            PointD top = UndistortPoint(new PointD(imagePoint.X, imagePoint.Y - epsilon));
            PointD bottom = UndistortPoint(new PointD(imagePoint.X, imagePoint.Y + epsilon));
            double scaleX = Distance(left, right) / (2 * epsilon);
            double scaleY = Distance(top, bottom) / (2 * epsilon);
            return double.IsFinite(scaleX) && double.IsFinite(scaleY)
                ? Math.Max(0.01, (scaleX + scaleY) / 2)
                : 1.0;
        }

        /// <summary>
        /// 对面积测量计算局部去畸变 Jacobian 的行列式，而不是简单地把平均长度比例平方。
        /// </summary>
        public double LocalAreaScaleFactor(PointD imagePoint)
        {
            if (!IsEnabled || !IsValid)
            {
                return 1.0;
            }

            const double epsilon = 0.5;
            PointD left = UndistortPoint(new PointD(imagePoint.X - epsilon, imagePoint.Y));
            PointD right = UndistortPoint(new PointD(imagePoint.X + epsilon, imagePoint.Y));
            PointD top = UndistortPoint(new PointD(imagePoint.X, imagePoint.Y - epsilon));
            PointD bottom = UndistortPoint(new PointD(imagePoint.X, imagePoint.Y + epsilon));
            double dxdu = (right.X - left.X) / (2 * epsilon);
            double dxdy = (bottom.X - top.X) / (2 * epsilon);
            double dydu = (right.Y - left.Y) / (2 * epsilon);
            double dydy = (bottom.Y - top.Y) / (2 * epsilon);
            double determinant = Math.Abs(dxdu * dydy - dxdy * dydu);
            return double.IsFinite(determinant) && determinant > 0
                ? determinant
                : 1.0;
        }

        private (double X, double Y) ToNormalized(PointD point)
        {
            return ((point.X - PrincipalX) / NormalizationRadius, (point.Y - PrincipalY) / NormalizationRadius);
        }

        private PointD FromNormalized(double x, double y)
        {
            return new PointD(PrincipalX + x * NormalizationRadius, PrincipalY + y * NormalizationRadius);
        }

        private (double X, double Y) ApplyDistortion(double x, double y)
        {
            double r2 = x * x + y * y;
            double radial = 1.0 + K1 * r2 + K2 * r2 * r2 + K3 * r2 * r2 * r2;
            double xd = x * radial + 2 * TangentialP1 * x * y + TangentialP2 * (r2 + 2 * x * x);
            double yd = y * radial + TangentialP1 * (r2 + 2 * y * y) + 2 * TangentialP2 * x * y;
            return (xd, yd);
        }

        private (double Dxdx, double Dxdy, double Dydx, double Dydy) DistortionJacobian(double x, double y)
        {
            double r2 = x * x + y * y;
            double radial = 1.0 + K1 * r2 + K2 * r2 * r2 + K3 * r2 * r2 * r2;
            double radialSlope = K1 + (2 * K2 * r2) + (3 * K3 * r2 * r2);
            double dXdx = radial + (2 * x * x * radialSlope) + (2 * TangentialP1 * y) + (6 * TangentialP2 * x);
            double dXdy = (2 * x * y * radialSlope) + (2 * TangentialP1 * x) + (2 * TangentialP2 * y);
            double dYdy = radial + (2 * y * y * radialSlope) + (6 * TangentialP1 * y) + (2 * TangentialP2 * x);
            // Brown-Conrady is irrotational here: the two mixed partials are equal.
            return (dXdx, dXdy, dXdy, dYdy);
        }

        private static double Distance(PointD left, PointD right)
        {
            double dx = right.X - left.X;
            double dy = right.Y - left.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 按图像尺寸自动设置主点（图像中心）与归一化半径（短边一半）并创建标定参数。
        /// English: Creates calibration with principal PointD and normalization radius inferred from image size.
        /// </summary>
        public static CameraCalibration CreateForImage(double k1, double k2, double imageWidth, double imageHeight)
        {
            return new CameraCalibration
            {
                K1 = k1,
                K2 = k2,
                PrincipalX = imageWidth > 0 ? imageWidth / 2.0 : 0,
                PrincipalY = imageHeight > 0 ? imageHeight / 2.0 : 0,
                NormalizationRadius = imageWidth > 0 && imageHeight > 0 ? Math.Min(imageWidth, imageHeight) / 2.0 : 1.0
            };
        }
    }
}
