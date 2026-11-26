using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Core.Measurements;
using ImageViewer.Controls;
using ImageViewer.Localization;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
[Trait("Category", "Wpf")]
public sealed class MenuSearchCommandTests
{
    [Fact]
    public void SearchResultsAreFlattenedWithBreadcrumbAndInvokeOriginalLeaf()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            var parent = new MenuItem { Header = "Custom tools" };
            var original = new MenuItem { Header = "Special action", IsEnabled = true };
            parent.Items.Add(original);
            viewer.mainContextMenu.Items.Add(parent);
            int invoked = 0;
            original!.Click += (_, _) => invoked++;

            viewer.menuSearchBox.Text = "special";
            WpfTestRunner.DrainDispatcher();

            var result = SearchResultFor(viewer, original);
            Assert.Equal("Custom tools › Special action", result.Header?.ToString());
            Assert.Equal(Visibility.Visible, viewer.menuSearchResultsHost.Visibility);
            Assert.Contains(result, viewer.mainContextMenu.Items.OfType<MenuItem>());
            Assert.Empty(result.Items);

            WpfTestRunner.InvokePrivate(viewer, "InvokeMenuSearchResult", result);
            Assert.Equal(1, invoked);
        });
    }

    [Fact]
    public void DisabledSearchResultIsVisibleButCannotInvokeOriginal()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            var group = new MenuItem { Header = "Measured profiles" };
            var original = new MenuItem
            {
                Header = UiText.Get("AdvancedMeasurementOperationStraightness"),
                Tag = new ImageViewerAdvancedMeasurementMenuTag(AdvancedMeasurementKind.Straightness),
                ToolTip = UiText.Get("MenuAliasesStraightness"),
                IsEnabled = false,
            };
            group.Items.Add(original);
            viewer.mainContextMenu.Items.Add(group);
            int invoked = 0;
            original.Click += (_, _) => invoked++;

            viewer.menuSearchBox.Text = "straightness";
            WpfTestRunner.DrainDispatcher();

            var result = SearchResultFor(viewer, original);
            Assert.False(result.IsEnabled);
            Assert.Equal("Measured profiles › " + original.Header, result.Header?.ToString());
            Assert.Contains(result, viewer.mainContextMenu.Items.OfType<MenuItem>());
            Assert.NotNull(result.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(result));
            WpfTestRunner.InvokePrivate(viewer, "InvokeMenuSearchResult", result);
            Assert.Equal(0, invoked);
        });
    }

    [Fact]
    public void ClearingSearchRestoresGroupedMenuVisibilityAndHidesResults()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            var syntheticGroup = new MenuItem { Header = "Custom group", Visibility = Visibility.Visible };
            var unrelated = new MenuItem { Header = "Custom command", Visibility = Visibility.Visible };
            syntheticGroup.Items.Add(unrelated);
            viewer.mainContextMenu.Items.Add(syntheticGroup);
            Visibility initial = unrelated.Visibility;

            viewer.menuSearchBox.Text = "fit";
            WpfTestRunner.DrainDispatcher();
            Assert.Equal(Visibility.Collapsed, syntheticGroup.Visibility);
            Assert.Equal(Visibility.Visible, viewer.menuSearchResultsHost.Visibility);
            Assert.Equal(Visibility.Visible, unrelated.Visibility);

            viewer.menuSearchBox.Clear();
            WpfTestRunner.DrainDispatcher();
            Assert.Equal(initial, unrelated.Visibility);
            Assert.Equal(Visibility.Collapsed, viewer.menuSearchResultsHost.Visibility);
            Assert.DoesNotContain(viewer.mainContextMenu.Items.OfType<MenuItem>(), item => item.Tag is MenuSearchResultTag);
        });
    }

    private static MenuItem SearchResultFor(ImageViewer.Controls.ImageViewer viewer, MenuItem original) =>
        viewer.mainContextMenu.Items.OfType<MenuItem>()
            .Single(item => item.Tag is MenuSearchResultTag tag && ReferenceEquals(tag.Original, original));

}
