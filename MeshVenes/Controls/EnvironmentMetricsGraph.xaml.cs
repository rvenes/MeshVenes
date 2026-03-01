using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MeshVenes.Models;
using Windows.UI;

namespace MeshVenes.Controls;

public sealed partial class EnvironmentMetricsGraph : UserControl
{
    private const double PlotPadding = 6.0;
    private static readonly SolidColorBrush GridStrokeBrush = new(Color.FromArgb(56, 255, 255, 255));
    private IReadOnlyList<EnvironmentMetricSample> _samples = Array.Empty<EnvironmentMetricSample>();
    private List<EnvironmentMetricSample> _sortedSamples = new();
    private EnvironmentMetricSample? _selectedSample;
    private EnvironmentMetricSample? _hoverSample;
    private DateTime _minTimestamp;
    private double _totalSeconds = 1;
    private double _tempMin;
    private double _tempMax;
    private double _pressureMin;
    private double _pressureMax;

    public EnvironmentMetricsGraph()
    {
        InitializeComponent();
        SizeChanged += EnvironmentMetricsGraph_SizeChanged;
    }

    public void SetSamples(IReadOnlyList<EnvironmentMetricSample> samples)
    {
        _samples = samples ?? Array.Empty<EnvironmentMetricSample>();
        RenderChart();
    }

    public void HighlightSample(DateTime timestampUtc)
    {
        if (_samples.Count == 0)
        {
            _selectedSample = null;
            _hoverSample = null;
            UpdateSelectionOverlay();
            return;
        }

        _selectedSample = FindNearestSampleByTimestamp(timestampUtc);
        _hoverSample = null;
        UpdateSelectionOverlay();
    }

    public void ClearHighlight()
    {
        _selectedSample = null;
        _hoverSample = null;
        UpdateSelectionOverlay();
    }

    private void EnvironmentMetricsGraph_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderChart();

    private void RenderChart()
    {
        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            GridCanvas.Children.Clear();
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            _sortedSamples = new();
            _hoverSample = null;
            HideSelectionOverlay();
            return;
        }

        RenderGrid(width, height);

        var samples = _samples
            .OrderBy(s => s.TimestampUtc)
            .ToList();
        _sortedSamples = samples;

        if (samples.Count == 0)
        {
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            _hoverSample = null;
            HideSelectionOverlay();
            return;
        }

        var minTs = samples.First().TimestampUtc;
        var maxTs = samples.Last().TimestampUtc;
        var midTs = minTs + TimeSpan.FromSeconds((maxTs - minTs).TotalSeconds / 2.0);
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);
        _minTimestamp = minTs;
        _totalSeconds = totalSeconds;
        AxisStartText.Text = FormatAxisTime(minTs);
        AxisMidText.Text = FormatAxisTime(midTs);
        AxisEndText.Text = FormatAxisTime(maxTs);

        var (tempMin, tempMax) = ResolveRange(samples.Select(s => s.TemperatureC), defaultMin: -20, defaultMax: 50, padding: 1);
        var (pressureMin, pressureMax) = ResolveRange(samples.Select(s => s.BarometricPressure), defaultMin: 950, defaultMax: 1050, padding: 0.5);
        _tempMin = tempMin;
        _tempMax = tempMax;
        _pressureMin = pressureMin;
        _pressureMax = pressureMax;
        var tempMid = tempMin + ((tempMax - tempMin) / 2.0);
        var pressureMid = pressureMin + ((pressureMax - pressureMin) / 2.0);

        TemperatureAxisTopText.Text = $"{tempMax:0.0}C";
        TemperatureAxisMidText.Text = $"{tempMid:0.0}C";
        TemperatureAxisBottomText.Text = $"{tempMin:0.0}C";

        HumidityAxisTopText.Text = "100%";
        HumidityAxisMidText.Text = "50%";
        HumidityAxisBottomText.Text = "0%";

        PressureAxisTopText.Text = $"{pressureMax:0.0}hPa";
        PressureAxisMidText.Text = $"{pressureMid:0.0}hPa";
        PressureAxisBottomText.Text = $"{pressureMin:0.0}hPa";

        TemperatureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.TemperatureC, tempMin, tempMax);

        HumidityLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.RelativeHumidity, 0, 100);

        PressureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.BarometricPressure, pressureMin, pressureMax);

        UpdateSelectionOverlay();
    }

    private void SetAxisValuesUnavailable()
    {
        TemperatureAxisTopText.Text = "—";
        TemperatureAxisMidText.Text = "—";
        TemperatureAxisBottomText.Text = "—";
        HumidityAxisTopText.Text = "—";
        HumidityAxisMidText.Text = "—";
        HumidityAxisBottomText.Text = "—";
        PressureAxisTopText.Text = "—";
        PressureAxisMidText.Text = "—";
        PressureAxisBottomText.Text = "—";
    }

    private void RenderGrid(double width, double height)
    {
        GridCanvas.Children.Clear();

        const int horizontalDivisions = 4;
        for (var i = 0; i <= horizontalDivisions; i++)
        {
            var y = PlotPadding + ((height - (2 * PlotPadding)) * i / horizontalDivisions);
            GridCanvas.Children.Add(new Line
            {
                X1 = PlotPadding,
                Y1 = y,
                X2 = width - PlotPadding,
                Y2 = y,
                Stroke = GridStrokeBrush,
                StrokeThickness = 1
            });
        }

        const int verticalDivisions = 4;
        for (var i = 0; i <= verticalDivisions; i++)
        {
            var x = PlotPadding + ((width - (2 * PlotPadding)) * i / verticalDivisions);
            GridCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = PlotPadding,
                X2 = x,
                Y2 = height - PlotPadding,
                Stroke = GridStrokeBrush,
                StrokeThickness = 1
            });
        }
    }

    private static string FormatAxisTime(DateTime timestamp)
    {
        if (timestamp == DateTime.MinValue)
            return "—";

        var local = timestamp.ToLocalTime();
        return local.ToString("HH:mm:ss");
    }

    private static (double Min, double Max) ResolveRange(
        IEnumerable<double?> values,
        double defaultMin,
        double defaultMax,
        double padding)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0)
            return (defaultMin, defaultMax);

        var min = present.Min() - padding;
        var max = present.Max() + padding;
        if (Math.Abs(max - min) < 0.001)
        {
            min -= 1;
            max += 1;
        }

        return (min, max);
    }

    private static PointCollection BuildPoints(
        IReadOnlyList<EnvironmentMetricSample> samples,
        double width,
        double height,
        double totalSeconds,
        DateTime minTs,
        Func<EnvironmentMetricSample, double?> selector,
        double minY,
        double maxY)
    {
        var points = new List<Windows.Foundation.Point>(samples.Count);
        var range = Math.Max(1e-6, maxY - minY);
        var usableWidth = Math.Max(1, width - (2 * PlotPadding));
        var usableHeight = Math.Max(1, height - (2 * PlotPadding));

        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (!value.HasValue)
                continue;

            var seconds = Math.Max(0, (sample.TimestampUtc - minTs).TotalSeconds);
            var x = PlotPadding + usableWidth * (seconds / totalSeconds);
            var clamped = Math.Max(minY, Math.Min(maxY, value.Value));
            var normalized = (clamped - minY) / range;
            var y = PlotPadding + usableHeight - (usableHeight * normalized);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        var maxPoints = Math.Max(64, (int)Math.Round(usableWidth * 2));
        if (points.Count <= maxPoints)
        {
            var all = new PointCollection();
            foreach (var point in points)
                all.Add(point);
            return all;
        }

        var reduced = new List<Windows.Foundation.Point>(maxPoints);
        var lastIndex = points.Count - 1;
        for (var i = 0; i < maxPoints; i++)
        {
            var index = (int)Math.Round(i * (lastIndex / (double)(maxPoints - 1)));
            reduced.Add(points[index]);
        }

        var downsampled = new PointCollection();
        foreach (var point in reduced)
            downsampled.Add(point);
        return downsampled;
    }

    private void PlotHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sortedSamples.Count == 0 || PlotHost.ActualWidth <= 0)
            return;

        var point = e.GetCurrentPoint(PlotHost).Position;
        var sample = FindNearestSampleByX(point.X);
        if (sample is null || ReferenceEquals(sample, _hoverSample))
            return;

        _hoverSample = sample;
        UpdateSelectionOverlay();
    }

    private void PlotHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverSample is null)
            return;

        _hoverSample = null;
        UpdateSelectionOverlay();
    }

    private EnvironmentMetricSample? FindNearestSampleByTimestamp(DateTime timestampUtc)
    {
        if (_sortedSamples.Count == 0)
            return null;

        EnvironmentMetricSample? nearest = null;
        long bestTicks = long.MaxValue;
        foreach (var sample in _sortedSamples)
        {
            var diff = Math.Abs((sample.TimestampUtc - timestampUtc).Ticks);
            if (diff >= bestTicks)
                continue;

            bestTicks = diff;
            nearest = sample;
        }

        return nearest ?? _sortedSamples[^1];
    }

    private EnvironmentMetricSample? FindNearestSampleByX(double x)
    {
        if (_sortedSamples.Count == 0)
            return null;

        var clampedX = Math.Max(PlotPadding, Math.Min(PlotHost.ActualWidth - PlotPadding, x));
        var seconds = ((clampedX - PlotPadding) / Math.Max(1, PlotHost.ActualWidth - (2 * PlotPadding))) * _totalSeconds;
        var target = _minTimestamp.AddSeconds(seconds);
        return FindNearestSampleByTimestamp(target);
    }

    private void UpdateSelectionOverlay()
    {
        if (_sortedSamples.Count == 0 || PlotHost.ActualWidth <= 0 || PlotHost.ActualHeight <= 0)
        {
            HideSelectionOverlay();
            return;
        }

        var sample = _hoverSample ?? _selectedSample;
        if (sample is null)
        {
            HideSelectionOverlay();
            return;
        }

        sample = FindNearestSampleByTimestamp(sample.TimestampUtc);
        if (sample is null)
        {
            HideSelectionOverlay();
            return;
        }

        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        var x = MapSampleX(sample.TimestampUtc, width);

        SelectionVerticalLine.X1 = x;
        SelectionVerticalLine.X2 = x;
        SelectionVerticalLine.Y1 = PlotPadding;
        SelectionVerticalLine.Y2 = Math.Max(PlotPadding, height - PlotPadding);
        SelectionVerticalLine.Visibility = Visibility.Visible;

        PositionMetricMarker(TemperatureMarker, x, MapSampleY(sample.TemperatureC, _tempMin, _tempMax, height));
        PositionMetricMarker(HumidityMarker, x, MapSampleY(sample.RelativeHumidity, 0, 100, height));
        PositionMetricMarker(PressureMarker, x, MapSampleY(sample.BarometricPressure, _pressureMin, _pressureMax, height));

        SelectionTooltipTime.Text = sample.TimestampLocal.ToString("ddd dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        SelectionTooltipTemperature.Text = $"Temperature: {FormatValue(sample.TemperatureC, "0.##", "C")}";
        SelectionTooltipHumidity.Text = $"Humidity: {FormatValue(sample.RelativeHumidity, "0.##", "%")}";
        SelectionTooltipPressure.Text = $"Pressure: {FormatValue(sample.BarometricPressure, "0.###", "hPa")}";
        PositionTooltip(x, height);
    }

    private static string FormatValue(double? value, string format, string unit)
        => value.HasValue
            ? $"{value.Value.ToString(format, CultureInfo.InvariantCulture)} {unit}"
            : "—";

    private static void PositionMetricMarker(Ellipse marker, double x, double? y)
    {
        if (!y.HasValue)
        {
            marker.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(marker, x - (marker.Width / 2.0));
        Canvas.SetTop(marker, y.Value - (marker.Height / 2.0));
        marker.Visibility = Visibility.Visible;
    }

    private void PositionTooltip(double x, double plotHeight)
    {
        SelectionTooltip.Visibility = Visibility.Visible;
        SelectionTooltip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = SelectionTooltip.DesiredSize;
        var maxX = Math.Max(8, PlotHost.ActualWidth - desired.Width - 8);
        var tooltipX = Math.Max(8, Math.Min(maxX, x + 14));
        var tooltipY = 10.0;
        if (tooltipX > PlotHost.ActualWidth * 0.62)
            tooltipY = Math.Max(8, Math.Min(plotHeight - desired.Height - 8, 18));

        Canvas.SetLeft(SelectionTooltip, tooltipX);
        Canvas.SetTop(SelectionTooltip, tooltipY);
    }

    private void HideSelectionOverlay()
    {
        SelectionVerticalLine.Visibility = Visibility.Collapsed;
        TemperatureMarker.Visibility = Visibility.Collapsed;
        HumidityMarker.Visibility = Visibility.Collapsed;
        PressureMarker.Visibility = Visibility.Collapsed;
        SelectionTooltip.Visibility = Visibility.Collapsed;
    }

    private double MapSampleX(DateTime timestampUtc, double width)
    {
        var usableWidth = Math.Max(1, width - (2 * PlotPadding));
        var seconds = Math.Max(0, (timestampUtc - _minTimestamp).TotalSeconds);
        var ratio = _totalSeconds <= 0 ? 0 : Math.Min(1, seconds / _totalSeconds);
        return PlotPadding + (usableWidth * ratio);
    }

    private static double? MapSampleY(double? value, double minY, double maxY, double height)
    {
        if (!value.HasValue)
            return null;

        var usableHeight = Math.Max(1, height - (2 * PlotPadding));
        var clamped = Math.Max(minY, Math.Min(maxY, value.Value));
        var range = Math.Max(1e-6, maxY - minY);
        var normalized = (clamped - minY) / range;
        return PlotPadding + usableHeight - (usableHeight * normalized);
    }
}
