namespace ImageViewer.Models
{
    /// <summary>
    /// 去畸变求解结果。
    /// Chinese: 报告还原点、是否达到残差阈值、已执行迭代次数与最终残差（像素）。
    /// English: Result of inverting the distortion model, including the convergence state,
    /// iteration count and final residual in pixels.
    /// </summary>
    public readonly record struct UndistortResult(PointD Point, bool Converged, int Iterations, double ResidualPixels);
}
