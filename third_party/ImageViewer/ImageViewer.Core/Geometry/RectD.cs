namespace ImageViewer.Models
{
    /// <summary>
    /// 与 UI 框架无关的矩形（左上角坐标 + 宽高）。
    /// Chinese: 供 Core 几何和持久化契约共享。
    /// English: Framework-neutral rectangle shared by Core geometry and persistence contracts.
    /// </summary>
    public readonly record struct RectD(double X, double Y, double Width, double Height)
    {
        /// <inheritdoc />
        public override string ToString() => $"({X}, {Y}, {Width}, {Height})";
    }
}
