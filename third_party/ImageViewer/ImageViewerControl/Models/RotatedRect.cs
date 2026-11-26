using System;

using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 旋转矩形 ROI
    /// Chinese: 表示一个可旋转的矩形 ROI，包含中心点、宽高和角度信息。
    /// English: Represents a rotated rectangle ROI with Center, Width, Height and Angle properties.
    /// </summary>
    public class RotatedRect : RoiBase
    {
        private PointD _center;
        private double _width;
        private double _height;
        private double _angle;

        public RotatedRect()
        {
            StrokeColor = RoiColors.Lime;
        }

        public PointD Center
        {
            get => _center;
            set => SetProperty(ref _center, value);
        }

        public double Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        public double Angle
        {
            get => _angle;
            set => SetProperty(ref _angle, value);
        }

        public override RoiBase Clone()
        {
            return new RotatedRect
            {
                Center = Center,
                Width = Width,
                Height = Height,
                Angle = Angle,
                Label = Label,
                StrokeColor = StrokeColor,
                StrokeThickness = StrokeThickness,
                IsVisible = IsVisible,
                IsLocked = IsLocked,
                Tolerance = Tolerance?.Clone(),
                MeasurementId = MeasurementId,
                AdvancedMeasurements = AdvancedMeasurements
            };
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not RotatedRect rect)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(RotatedRect)}.", nameof(source));
            }

            Center = rect.Center;
            Width = rect.Width;
            Height = rect.Height;
            Angle = rect.Angle;
            ApplyCommonState(rect);
        }
        /// <summary>
        /// 克隆当前 RotatedRect 实例。
        /// Chinese: 返回表示此旋转矩形当前状态的副本，供撤销/重做或状态备份使用。
        /// English: Creates a copy of this RotatedRect instance for undo/redo or snapshot purposes.
        /// </summary>
        /// <returns>RotatedRect 的副本 / A copy of the RotatedRect.</returns>
    }
}
