using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ImageViewer.Core.Spc;
using ImageViewer.Localization;

namespace ImageViewer.Controls;

/// <summary>
/// 自绘 Xbar-R 控制图。
/// Chinese: 不用第三方图表库：控制图的元素很少（控制限、折线、点、判异标记），自绘反而能精确控制
/// 判异点的语义着色与中文标注。所有文字用 SystemColors，跟随控件主题。
/// English: A hand-drawn chart keeps violation semantics and labels explicit without a charting dependency.
/// </summary>
internal static class ControlChartRenderer
{
    private const double DefaultWidth = 520;
    private const double DefaultHeight = 180;
    private const double LeftMargin = 66;
    private const double RightMargin = 18;
    private const double TopMargin = 30;
    private const double BottomMargin = 26;

    public static void Draw(Canvas canvas, XbarRangeControlChart chart, SpcControlChartKind kind, string title)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(chart);

        canvas.Children.Clear();
        (double width, double height) = GetChartSize(canvas);
        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        ControlChartLimits limits = kind == SpcControlChartKind.Mean ? chart.MeanLimits : chart.RangeLimits;
        double[] values = kind == SpcControlChartKind.Mean
            ? chart.Points.Select(static point => point.Mean).ToArray()
            : chart.Points.Select(static point => point.Range).ToArray();

        AddTitle(canvas, title);
        double upperBound = Math.Max(limits.UpperControlLimit, values.Max());
        double lowerBound = Math.Min(limits.LowerControlLimit, values.Min());
        double span = upperBound - lowerBound;
        if (span <= 0)
        {
            span = Math.Max(Math.Abs(upperBound) * 0.1, 1);
        }

        upperBound += span * 0.08;
        lowerBound -= span * 0.08;
        double range = upperBound - lowerBound;

        double MapY(double value) => TopMargin + (upperBound - value) / range * plotHeight;
        double MapX(int index) => values.Length == 1
            ? LeftMargin + plotWidth / 2
            : LeftMargin + index * plotWidth / (values.Length - 1);

        AddLimitLine(canvas, width, limits.UpperControlLimit, "UCL", Brushes.IndianRed, MapY);
        AddLimitLine(canvas, width, limits.CenterLine, "CL", SystemColors.GrayTextBrush, MapY);
        AddLimitLine(canvas, width, limits.LowerControlLimit, "LCL", Brushes.IndianRed, MapY);

        var polyline = new Polyline
        {
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (int index = 0; index < values.Length; index++)
        {
            polyline.Points.Add(new Point(MapX(index), MapY(values[index])));
        }

        canvas.Children.Add(polyline);

        Dictionary<int, List<SpcControlChartRule>> rulesByPoint = CollectRules(chart, kind);
        for (int index = 0; index < values.Length; index++)
        {
            bool hasViolation = rulesByPoint.ContainsKey(index);
            var marker = new Ellipse
            {
                Width = hasViolation ? 7 : 5,
                Height = hasViolation ? 7 : 5,
                Fill = hasViolation ? Brushes.Red : SystemColors.ControlTextBrush,
                ToolTip = BuildPointToolTip(values[index], rulesByPoint, index)
            };
            double x = MapX(index);
            double y = MapY(values[index]);
            canvas.Children.Add(marker);
            Canvas.SetLeft(marker, x - marker.Width / 2);
            Canvas.SetTop(marker, y - marker.Height / 2);
        }

        AddXAxisLabels(canvas, values.Length, MapX, height);
    }

    public static void DrawEmpty(Canvas canvas, string message)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Children.Clear();
        (double width, double height) = GetChartSize(canvas);
        var text = new TextBlock
        {
            Text = message,
            Width = width,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = SystemColors.GrayTextBrush
        };
        canvas.Children.Add(text);
        Canvas.SetTop(text, Math.Max(TopMargin, height / 2 - 10));
    }

    private static void AddTitle(Canvas canvas, string title)
    {
        var text = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.ControlTextBrush
        };
        canvas.Children.Add(text);
        Canvas.SetLeft(text, LeftMargin);
        Canvas.SetTop(text, 6);
    }

    private static void AddLimitLine(Canvas canvas, double width, double value, string name, Brush brush, Func<double, double> mapY)
    {
        double y = mapY(value);
        var line = new Line
        {
            X1 = LeftMargin,
            X2 = width - RightMargin,
            Y1 = y,
            Y2 = y,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = name == "CL" ? null : new DoubleCollection { 4, 3 }
        };
        canvas.Children.Add(line);

        var label = new TextBlock
        {
            Text = $"{name} {value.ToString("G5", CultureInfo.CurrentCulture)}",
            Width = LeftMargin - 8,
            TextAlignment = TextAlignment.Right,
            FontSize = 10,
            Foreground = brush
        };
        canvas.Children.Add(label);
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, y - 7);
    }

    private static void AddXAxisLabels(Canvas canvas, int count, Func<int, double> mapX, double height)
    {
        int step = Math.Max(1, (int)Math.Ceiling(count / 10d));
        for (int index = 0; index < count; index += step)
        {
            var label = new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.CurrentCulture),
                Width = 36,
                TextAlignment = TextAlignment.Center,
                FontSize = 9,
                Foreground = SystemColors.GrayTextBrush
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, mapX(index) - 18);
            Canvas.SetTop(label, height - BottomMargin + 6);
        }
    }

    private static Dictionary<int, List<SpcControlChartRule>> CollectRules(XbarRangeControlChart chart, SpcControlChartKind kind)
    {
        var rulesByPoint = new Dictionary<int, List<SpcControlChartRule>>();
        foreach (ControlChartViolation violation in chart.Violations)
        {
            if (violation.Chart != kind)
            {
                continue;
            }

            if (!rulesByPoint.TryGetValue(violation.PointIndex, out List<SpcControlChartRule>? rules))
            {
                rules = [];
                rulesByPoint.Add(violation.PointIndex, rules);
            }

            rules.Add(violation.Rule);
        }

        return rulesByPoint;
    }

    private static string BuildPointToolTip(double value, Dictionary<int, List<SpcControlChartRule>> rulesByPoint, int index)
    {
        string text = value.ToString("G5", CultureInfo.CurrentCulture);
        if (!rulesByPoint.TryGetValue(index, out List<SpcControlChartRule>? rules))
        {
            return text;
        }

        return string.Join(Environment.NewLine, rules.Select(rule => UiText.Get(DescribeRuleKey(rule))).Prepend(text));
    }

    private static string DescribeRuleKey(SpcControlChartRule rule) => rule switch
    {
        SpcControlChartRule.BeyondControlLimit => "SpcRuleBeyondControlLimit",
        SpcControlChartRule.TwoOfThreeBeyondTwoSigma => "SpcRuleTwoOfThreeBeyondTwoSigma",
        SpcControlChartRule.FourOfFiveBeyondOneSigma => "SpcRuleFourOfFiveBeyondOneSigma",
        SpcControlChartRule.EightInARowOnOneSide => "SpcRuleEightInARowOnOneSide",
        SpcControlChartRule.SixInARowTrending => "SpcRuleSixInARowTrending",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
    };

    private static (double Width, double Height) GetChartSize(Canvas canvas)
    {
        double width = GetLength(canvas.ActualWidth, canvas.Width, DefaultWidth);
        double height = GetLength(canvas.ActualHeight, canvas.Height, DefaultHeight);
        return (width, height);
    }

    private static double GetLength(double actual, double configured, double fallback)
    {
        if (actual > 0)
        {
            return actual;
        }

        return double.IsNaN(configured) || configured <= 0 ? fallback : configured;
    }
}
