using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ImageViewer.Core.Measurements;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests;

public class AdvancedMeasurementDialogTests
{
    [Fact]
    public void ConstructorBuildsAccessibleResizableWorkbenchWithEveryOperation()
    {
        WpfTestRunner.Run(() =>
        {
            var rois = new List<RoiBase> { new LineMeasureRoi { Label = "Edge A", P1 = new PointD(0, 0), P2 = new PointD(10, 0) } };
            var dialog = CreateDialog(rois);

            Assert.Equal(ResizeMode.CanResize, dialog.ResizeMode);
            Assert.Equal(UiText.Get("AdvancedMeasurementDialogTitle"), AutomationProperties.GetName(dialog));
            ComboBox operationCombo = FindVisualChildren<ComboBox>(dialog).Single();
            Assert.Equal(Enum.GetValues<AdvancedMeasurementKind>().Length, operationCombo.Items.Count);
            string firstDisplayName = (string)operationCombo.Items[0].GetType().GetProperty("DisplayName")!.GetValue(operationCombo.Items[0])!;
            Assert.Equal(UiText.Get("AdvancedMeasurementOperationMidpoint"), firstDisplayName);
            object periodicPitch = operationCombo.Items.Cast<object>().Single(item =>
                (AdvancedMeasurementKind)item.GetType().GetProperty("Kind")!.GetValue(item)! == AdvancedMeasurementKind.PeriodicPitch);
            Assert.Equal(
                UiText.Get("AdvancedMeasurementOperationPeriodicPitch"),
                periodicPitch.GetType().GetProperty("DisplayName")!.GetValue(periodicPitch));
            Assert.True(FindVisualChildren<ListBox>(dialog).Count() >= 4);
            Assert.Single(FindVisualChildren<Button>(dialog), button =>
                AutomationProperties.GetName(button) == UiText.Get("AdvancedMeasurementExport"));

            dialog.Close();
        });
    }

    [Fact]
    public void RefreshSourceInventoryAndEvaluationHandleAnEmptyWorkbench()
    {
        WpfTestRunner.Run(() =>
        {
            var dialog = CreateDialog([]);

            WpfTestRunner.InvokePrivate(dialog, "RefreshSourceInventory");
            WpfTestRunner.InvokePrivate(dialog, "EvaluateAll");

            ListBox[] lists = FindVisualChildren<ListBox>(dialog).ToArray();
            Assert.Empty(lists[0].Items);
            Assert.Empty(lists[^2].Items);
            Assert.Empty(lists[^1].Items);
            dialog.Close();
        });
    }

    [Fact]
    public void SavingEditedDefinitionPreservesItsIdentityAndReplacesItInPlace()
    {
        WpfTestRunner.Run(() =>
        {
            var first = new LineMeasureRoi { Label = "First", P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
            var second = new LineMeasureRoi { Label = "Second", P1 = new PointD(0, 2), P2 = new PointD(10, 2) };
            Guid definitionId = Guid.NewGuid();
            first.AdvancedMeasurements =
            [
                new AdvancedMeasurementDefinition
                {
                    Id = definitionId,
                    Name = "Original midpoint",
                    Kind = AdvancedMeasurementKind.Midpoint,
                    Sources = [first.MeasurementId, second.MeasurementId]
                }
            ];
            AdvancedMeasurementDefinition[]? callbackDefinitions = null;
            RoiBase? callbackOwner = null;
            var dialog = new AdvancedMeasurementDialog(
                () => [first, second],
                () => 0.05,
                () => "mm",
                () => null,
                (owner, definitions) =>
                {
                    callbackOwner = owner;
                    callbackDefinitions = definitions;
                    owner.AdvancedMeasurements = definitions;
                });

            ListBox savedList = FindVisualChildren<ListBox>(dialog).ElementAt(2);
            savedList.SelectedItem = savedList.Items.Cast<AdvancedMeasurementDefinition>().Single();
            TextBox nameTextBox = FindVisualChildren<TextBox>(dialog).First();
            nameTextBox.Text = "Edited midpoint";
            WpfTestRunner.InvokePrivate(dialog, "AddOrUpdateDefinition");

            Assert.Same(first, callbackOwner);
            AdvancedMeasurementDefinition persisted = Assert.Single(callbackDefinitions!);
            Assert.Equal(definitionId, persisted.Id);
            Assert.Equal("Edited midpoint", persisted.Name);
            Assert.Equal(new[] { first.MeasurementId, second.MeasurementId }, persisted.Sources);
            dialog.Close();
        });
    }

    [Fact]
    public void ClosingWorkbenchDisposesLiveRoiSubscriptions()
    {
        WpfTestRunner.Run(() =>
        {
            var roi = new LineMeasureRoi { Label = "Edge A", P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
            var dialog = CreateDialog([roi]);

            dialog.Close();
            roi.Label = "Updated edge";

            var disposedField = typeof(AdvancedMeasurementDialog).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(disposedField);
            Assert.True((bool)disposedField!.GetValue(dialog)!);
        });
    }

    [Fact]
    public void WitnessPreviewIncludesExtractedContourSeparatelyFromSearchBounds()
    {
        WpfTestRunner.Run(() =>
        {
            var roi = new RotatedRect { Label = "Search bounds", Center = new PointD(5, 5), Width = 10, Height = 10 };
            var dialog = CreateDialog([roi]);
            var definition = new AdvancedMeasurementDefinition { Name = "Shape", Kind = AdvancedMeasurementKind.ShapeMetrics, Sources = [roi.MeasurementId] };
            var evaluation = new AdvancedMeasurementEvaluation
            {
                Id = definition.Id,
                Success = true,
                SourceContours = [[new PointD(2, 2), new PointD(4, 2), new PointD(4, 3), new PointD(2, 3)]]
            };
            Type rowType = typeof(AdvancedMeasurementDialog).GetNestedType("EvaluationRow", System.Reflection.BindingFlags.NonPublic)!;
            object row = Activator.CreateInstance(rowType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: [definition, evaluation, "Shape"], culture: null)!;
            var canvas = (Canvas)typeof(AdvancedMeasurementDialog).GetField("_witnessCanvas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;

            WpfTestRunner.InvokePrivate(dialog, "DrawWitness", canvas, row);

            Assert.Contains(canvas.Children.OfType<System.Windows.Shapes.Polyline>(), line =>
                line.Stroke == System.Windows.Media.Brushes.OrangeRed && line.Points.Count == 5);
            Assert.Contains(canvas.Children.OfType<System.Windows.Shapes.Polyline>(), line =>
                line.Stroke == System.Windows.Media.Brushes.DimGray);
            dialog.Close();
        });
    }

    [Fact]
    public void SamplePreviewRedrawsWhenItsCanvasChangesSize()
    {
        WpfTestRunner.Run(() =>
        {
            var dialog = CreateDialog([]);
            var definition = new AdvancedMeasurementDefinition { Name = "Profile", Kind = AdvancedMeasurementKind.GapProfile };
            var evaluation = new AdvancedMeasurementEvaluation
            {
                Id = definition.Id,
                Success = true,
                Stations = [0, 1, 2],
                Samples = [1, 2, 1]
            };
            Type rowType = typeof(AdvancedMeasurementDialog).GetNestedType("EvaluationRow", System.Reflection.BindingFlags.NonPublic)!;
            object row = Activator.CreateInstance(rowType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: [definition, evaluation, "Profile"], culture: null)!;
            var rows = (System.Collections.IList)typeof(AdvancedMeasurementDialog).GetField("_evaluationRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
            rows.Add(row);
            var results = (ListBox)typeof(AdvancedMeasurementDialog).GetField("_resultsList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
            results.SelectedItem = row;
            var canvas = (Canvas)typeof(AdvancedMeasurementDialog).GetField("_samplesCanvas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
            canvas.Width = 120;
            canvas.Height = 100;
            canvas.Measure(new Size(120, 100));
            canvas.Arrange(new Rect(0, 0, 120, 100));

            WpfTestRunner.InvokePrivate(dialog, "DrawSamples", canvas, evaluation.Stations, evaluation.Samples);
            Point[] firstPlot = canvas.Children.OfType<System.Windows.Shapes.Polyline>().Single().Points.ToArray();
            canvas.Width = 300;
            canvas.Height = 200;
            canvas.Measure(new Size(300, 200));
            canvas.Arrange(new Rect(0, 0, 300, 200));
            WpfTestRunner.InvokePrivate(dialog, "OnCanvasSizeChanged", canvas, null);
            Point[] resizedPlot = canvas.Children.OfType<System.Windows.Shapes.Polyline>().Single().Points.ToArray();

            Assert.NotEqual(firstPlot[1].X, resizedPlot[1].X);
            Assert.NotEqual(firstPlot[0].Y, resizedPlot[0].Y);
            dialog.Close();
        });
    }

    [Fact]
    public void GapProfileWitnessDrawsEachNormalPairIndependently()
    {
        WpfTestRunner.Run(() =>
        {
            var dialog = CreateDialog([]);
            var definition = new AdvancedMeasurementDefinition { Name = "Gap", Kind = AdvancedMeasurementKind.GapProfile };
            var evaluation = new AdvancedMeasurementEvaluation
            {
                Id = definition.Id,
                Success = true,
                Geometry = [new PointD(0, 0), new PointD(0, 1), new PointD(2, 0), new PointD(2, 2)]
            };
            Type rowType = typeof(AdvancedMeasurementDialog).GetNestedType("EvaluationRow", System.Reflection.BindingFlags.NonPublic)!;
            object row = Activator.CreateInstance(rowType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: [definition, evaluation, "Gap"], culture: null)!;
            var canvas = (Canvas)typeof(AdvancedMeasurementDialog).GetField("_witnessCanvas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;

            WpfTestRunner.InvokePrivate(dialog, "DrawWitness", canvas, row);

            System.Windows.Shapes.Polyline[] connectors = canvas.Children.OfType<System.Windows.Shapes.Polyline>()
                .Where(line => line.Stroke == System.Windows.Media.Brushes.DodgerBlue).ToArray();
            Assert.Equal(2, connectors.Length);
            Assert.All(connectors, connector => Assert.Equal(2, connector.Points.Count));
            dialog.Close();
        });
    }

    private static AdvancedMeasurementDialog CreateDialog(IReadOnlyList<RoiBase> rois)
    {
        return new AdvancedMeasurementDialog(
            () => rois,
            () => 0.05,
            () => "mm",
            () => null,
            (_, _) => { });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object childObject in LogicalTreeHelper.GetChildren(root))
        {
            if (childObject is not DependencyObject child)
            {
                continue;
            }

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
