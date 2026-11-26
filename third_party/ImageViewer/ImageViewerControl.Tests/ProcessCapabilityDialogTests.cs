using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

public class ProcessCapabilityDialogTests
{
    [Fact]
    public void ConstructorShowsCapabilityAndDrawsBothCharts()
    {
        WpfTestRunner.Run(() =>
        {
            var series = new SpcMeasurementSeries("k", "长度", "mm", [9, 10, 11, 10, 9, 10], 7, 13);

            var dialog = new ProcessCapabilityDialog([series]);

            Assert.Equal(UiText.Get("ProcessCapabilityDialogTitle"), AutomationProperties.GetName(dialog));
            Assert.Equal("6", GetText(dialog, "_sampleCountText"));
            Assert.NotEqual("—", GetText(dialog, "_cpText"));
            Assert.NotEqual("—", GetText(dialog, "_cpkText"));
            var subgroupCombo = GetField<ComboBox>(dialog, "_subgroupCombo");
            Assert.Equal(2, subgroupCombo.Items.Count);
            Assert.NotEmpty(GetField<Canvas>(dialog, "_meanCanvas").Children);
            Assert.NotEmpty(GetField<Canvas>(dialog, "_rangeCanvas").Children);
            dialog.Close();
        });
    }

    [Fact]
    public void ConstructorExplainsWhyThereIsNothingToAnalyze()
    {
        WpfTestRunner.Run(() =>
        {
            var dialog = new ProcessCapabilityDialog([]);

            Assert.Equal(UiText.Get("ProcessCapabilityNoSeries"), GetText(dialog, "_statusText"));
            Assert.Equal("—", GetText(dialog, "_cpText"));
            dialog.Close();
        });
    }

    [Fact]
    public void ConstructorFallsBackToCapabilityOnlyWhenSubgroupsAreTooShort()
    {
        WpfTestRunner.Run(() =>
        {
            var series = new SpcMeasurementSeries("k", "长度", "mm", [1, 2, 3], null, null);

            var dialog = new ProcessCapabilityDialog([series]);

            Assert.Equal(UiText.Get("ProcessCapabilityInsufficientChartData"), GetText(dialog, "_statusText"));
            Assert.Equal("3", GetText(dialog, "_sampleCountText"));
            Assert.Equal("—", GetText(dialog, "_cpText"));
            dialog.Close();
        });
    }

    private static T GetField<T>(ProcessCapabilityDialog dialog, string fieldName) =>
        (T)dialog.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static string GetText(ProcessCapabilityDialog dialog, string fieldName) =>
        GetField<TextBlock>(dialog, fieldName).Text;
}
