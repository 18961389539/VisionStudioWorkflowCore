namespace ImageViewer.Models
{
    /// <summary>
    /// 卡尺接受的边缘极性。
    /// Chinese: 极性按剖面沿测量方向的灰度变化定义，与直线/圆卡尺共用。
    /// English: Edge polarity accepted by a caliper, defined by the gray-value change
    /// along the measurement direction and shared by line and circular calipers.
    /// </summary>
    public enum CaliperEdgePolarity
    {
        /// <summary>任意极性。</summary>
        Any,

        /// <summary>由暗到亮的上升沿。</summary>
        DarkToLight,

        /// <summary>由亮到暗的下降沿。</summary>
        LightToDark
    }
}
