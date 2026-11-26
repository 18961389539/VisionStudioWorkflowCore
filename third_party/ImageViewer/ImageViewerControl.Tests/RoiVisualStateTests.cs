using System.Collections.Generic;
using System.ComponentModel;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// ROI 共享视觉状态的存储与复制语义测试。
    /// Chinese: 重点守住"属性变更通知"——ROI 列表的可见/锁定复选框与显示名都依赖它。
    /// English: Storage and copy semantics of the shared ROI visual state. The notification assertions
    /// guard the ROI-list visibility/lock checkboxes and the display name, which are XAML-bound.
    /// </summary>
    public class RoiVisualStateTests
    {
        [Fact]
        public void DefaultVisualState_IsCyanUnlockedAndVisible()
        {
            // PolygonRoi 未在构造函数里覆盖默认色，因此可用来断言基类默认值。
            var roi = new PolygonRoi();

            Assert.Equal(RoiColors.Cyan, roi.StrokeColor);
            Assert.Equal(2.0, roi.StrokeThickness, 6);
            Assert.True(roi.IsVisible);
            Assert.False(roi.IsLocked);
            Assert.False(roi.IsSelected);
        }

        [Fact]
        public void DerivedRoi_KeepsItsOwnDefaultStrokeColor()
        {
            Assert.Equal(RoiColors.Gold, new CircleRoi().StrokeColor);
            Assert.Equal(RoiColors.LightGreen, new PolylineRoi().StrokeColor);
            Assert.Equal(RoiColors.Lime, new RotatedRect().StrokeColor);
        }

        [Fact]
        public void StrokeColor_Set_TriggersPropertyChanged()
        {
            var roi = new CircleRoi();

            AssertNotifies(roi, nameof(RoiBase.StrokeColor), () => roi.StrokeColor = RoiColors.Red);
            Assert.Equal(RoiColors.Red, roi.StrokeColor);
        }

        [Fact]
        public void StrokeThickness_Set_TriggersPropertyChanged()
        {
            var roi = new CircleRoi();

            AssertNotifies(roi, nameof(RoiBase.StrokeThickness), () => roi.StrokeThickness = 4.5);
        }

        [Fact]
        public void IsVisible_Set_TriggersPropertyChanged()
        {
            var roi = new CircleRoi();

            AssertNotifies(roi, nameof(RoiBase.IsVisible), () => roi.IsVisible = false);
        }

        [Fact]
        public void IsLocked_Set_TriggersPropertyChanged()
        {
            var roi = new CircleRoi();

            AssertNotifies(roi, nameof(RoiBase.IsLocked), () => roi.IsLocked = true);
        }

        [Fact]
        public void SettingSameValue_DoesNotTriggerPropertyChanged()
        {
            var roi = new CircleRoi();
            var changed = new List<string?>();
            roi.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            roi.StrokeColor = roi.StrokeColor;
            roi.StrokeThickness = roi.StrokeThickness;
            roi.IsVisible = roi.IsVisible;
            roi.IsLocked = roi.IsLocked;

            Assert.Empty(changed);
        }

        [Fact]
        public void Label_Set_AlsoNotifiesDisplayName()
        {
            var roi = new CircleRoi();
            var changed = new List<string?>();
            roi.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            roi.Label = "圆1";

            Assert.Contains(nameof(RoiBase.Label), changed);
            Assert.Contains(nameof(RoiBase.DisplayName), changed);
        }

        [Fact]
        public void CopyVisualStateFrom_CopiesSharedState()
        {
            var source = new CircleRoi
            {
                StrokeColor = RoiColors.Magenta,
                StrokeThickness = 3.5,
                IsVisible = false,
                IsLocked = true
            };
            var target = new CircleRoi();

            target.CopyVisualStateFrom(source);

            Assert.Equal(RoiColors.Magenta, target.StrokeColor);
            Assert.Equal(3.5, target.StrokeThickness, 6);
            Assert.False(target.IsVisible);
            Assert.True(target.IsLocked);
        }

        [Fact]
        public void CopyVisualStateFrom_DoesNotCopySelection()
        {
            var source = new CircleRoi { IsSelected = true };
            var target = new CircleRoi { IsSelected = false };

            target.CopyVisualStateFrom(source);

            Assert.False(target.IsSelected);
        }

        [Fact]
        public void Clone_CopiesSharedVisualState()
        {
            var roi = new CircleRoi
            {
                Center = new PointD(10, 20),
                Radius = 5,
                StrokeColor = RoiColors.Orchid,
                StrokeThickness = 4,
                IsVisible = false,
                IsLocked = true
            };

            var clone = Assert.IsType<CircleRoi>(roi.Clone());

            Assert.Equal(RoiColors.Orchid, clone.StrokeColor);
            Assert.Equal(4, clone.StrokeThickness, 6);
            Assert.False(clone.IsVisible);
            Assert.True(clone.IsLocked);
        }

        private static void AssertNotifies(INotifyPropertyChanged source, string propertyName, System.Action act)
        {
            var changed = new List<string?>();
            source.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            act();

            Assert.Contains(propertyName, changed);
        }
    }
}
