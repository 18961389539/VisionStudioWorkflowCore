using ImageViewer.Models;

namespace ImageViewer.Models
{
    /// <summary>
    /// 像素/物理尺寸校准对话框的返回结果。
    /// Chinese: Length 为用户输入的实际长度, Unit 为物理单位；Distortion 为对话框采集到的完整
    /// Brown-Conrady 畸变模型（含 K3/P1/P2、主点、归一化半径）。Draft 保留 K1/K2 两个便捷属性，
    /// 便于既有调用方与测试继续按标量工作。
    /// English: Result produced by the calibration dialog. Distortion carries the complete
    /// Brown-Conrady model captured by the dialog; K1/K2 remain as convenience forwarders.
    /// </summary>
    public readonly record struct CalibrationDialogResult(double Length, string Unit, CameraCalibration? Distortion)
    {
        /// <summary>一阶径向畸变系数；未配置畸变时为 0。</summary>
        public double K1 => Distortion?.K1 ?? 0;

        /// <summary>二阶径向畸变系数；未配置畸变时为 0。</summary>
        public double K2 => Distortion?.K2 ?? 0;
    }
}
