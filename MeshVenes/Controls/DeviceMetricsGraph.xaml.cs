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

public sealed partial class DeviceMetricsGraph : UserControl
{
    private const double PlotPadding = 6.0;
    private static readonly SolidColorBrush GridStrokeBrush = new(Color.FromArgb(56, 255, 255, 255));

    private IReadOnlyList<DeviceMetricSample> _samples = Array.Empty<DeviceMetricSample>();
    private List<DeviceMetricSample> _sortedSamples = new();
    private DeviceMetricSample? _selectedSample;
    private DeviceMetricSample? _hoverSample;
    private DateTime _minTimestamp;
    private double _totalSeconds = 1;
    private bool _hasVoltageSeries;
    private double _batteryMin;
    private double _batteryMax;

    public DeviceMetricsGraph()
    {
        InitializeComponent();
        SizeChanged += DeviceMetricsGraph_SizeChanged;
    }

    public void SetSamples(IReadOnlyList<DeviceMetricSample> samples)
    {
        _samples = samples ?? Array.Empty<DeviceMetricSample>();
        RenderChart();
    }

    public void HighlightSample(DateTime timestamp)
    {
        if (_samples.Count == 0)
        {
            _selectedSample = null;
            _hoverSample = null;
            UpdateSelectionOverlay();
            return;
        }

        _selectedSample = FindNearestSampleByTimestamp(timestamp);
        _hoverSample = null;
        UpdateSelectionOverlay();
    }

    public void ClearHighlight()
    {
        _selectedSample = null;
        _hoverSample = null;
        UpdateSelectionOverlay();
    }

    private void DeviceMetricsGraph_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderChart();

    private void RenderChart()
    {
        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            GridCanvas.Children.Clear();
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
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
            .OrderBy(s => s.Timestamp)
            .ToList();
        _sortedSamples = samples;

        if (samples.Count == 0)
        {
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            _hoverSample = null;
            HideSelectionOverlay();
            return;
        }

        var minTs = samples.First().Timestamp;
        var maxTs = samples.Last().Timestamp;
        var midTs = minTs + TimeSpan.FromSeconds((maxTs - minTs).TotalSeconds / 2.0);
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);
        _minTimestamp = minTs;
        _totalSeconds = totalSeconds;
        AxisStartText.Text = FormatAxisTime(minTs);
        AxisMidText.Text = FormatAxisTime(midTs);
        AxisEndText.Text = FormatAxisTime(maxTs);

        var hasVoltage = samples.Any(s => s.BatteryVolts.HasValue);
        _hasVoltageSeries = hasVoltage;
        var batteryMin = hasVoltage ? 3.0 : 0.0;
        var batteryMax = hasVoltage ? 4.3 : 100.0;
        _batteryMin = batteryMin;
        _batteryMax = batteryMax;
        var batteryMid = batteryMin + ((batteryMax - batteryMin) / 2.0);
        BatteryAxisTopText.Text = hasVoltage ? $"{batteryMax:0.0}V" : $"{batteryMax:0}%";
        BatteryAxisMidText.Text = hasVoltage ? $"{batteryMid:0.0}V" : $"{batteryMid:0}%";
        BatteryAxisBottomText.Text = hasVoltage ? $"{batteryMin:0.0}V" : $"{batteryMin:0}%";
        ChannelAxisTopText.Text = "100%";
        ChannelAxisMidText.Text = "50%";
        ChannelAxisBottomText.Text = "0%";
        AirtimeAxisTopText.Text = "100%";
        AirtimeAxisMidText.Text = "50%";
        AirtimeAxisBottomText.Text = "0%";

        BatteryLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => hasVoltage ? sample.BatteryVolts : sample.BatteryPercent,
            batteryMin, batteryMax);

        ChannelLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => sample.ChannelUtilization,
            0, 100);

        AirtimeLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => sample.Airtime,
            0, 100);

        UpdateSelectionOverlay();
    }

    private void SetAxisValuesUnavailable()
    {
        BatteryAxisTopText.Text = "—";
        BatteryAxisMidText.Text = "—";
        BatteryAxisBottomText.Text = "—";
        ChannelAxisTopText.Text = "—";
        ChannelAxisMidText.Text = "—";
        ChannelAxisBottomText.Text = "—";
        AirtimeAxisTopText.Text = "—";
        AirtimeAxisMidText.Text = "—";
        AirtimeAxisBottomText.Text = "—";
    }

    private void RenderGrid(double width, double height)
    {
        GridCanvas.Children.Clear();

        var horizontalDivisions = 4;
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

        var verticalDivisions = 4;
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

    private static PointCollection BuildPoints(
        IReadOnlyList<DeviceMetricSample> samples,
        double width,
        double height,
        double totalSeconds,
        DateTime minTs,
        Func<DeviceMetricSample, double?> selector,
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

            var seconds = Math.Max(0, (sample.Timestamp - minTs).TotalSeconds);
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

    private DeviceMetricSample? FindNearestSampleByTimestamp(DateTime timestamp)
    {
        if (_sortedSamples.Count == 0)
            return null;

        DeviceMetricSample? nearest = null;
        long bestTicks = long.MaxValue;
        foreach (var sample in _sortedSamples)
        {
            var diff = Math.Abs((sample.Timestamp - timestamp).Ticks);
            if (diff >= bestTicks)
                continue;

            bestTicks = diff;
            nearest = sample;
        }

        return nearest ?? _sortedSamples[^1];
    }

    private DeviceMetricSample? FindNearestSampleByX(double x)
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

        sample = FindNearestSampleByTimestamp(sample.Timestamp);
        if (sample is null)
        {
            HideSelectionOverlay();
            return;
        }

        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        var x = MapSampleX(sample.Timestamp, width);

        SelectionVerticalLine.X1 = x;
        SelectionVerticalLine.X2 = x;
        SelectionVerticalLine.Y1 = PlotPadding;
        SelectionVerticalLine.Y2 = Math.Max(PlotPadding, height - PlotPadding);
        SelectionVerticalLine.Visibility = Visibility.Visible;

        PositionMetricMarker(BatteryMarker, x, MapSampleY(GetBatteryValue(sample), _batteryMin, _batteryMax, height));
        PositionMetricMarker(ChannelMarker, x, MapSampleY(sample.ChannelUtilization, 0, 100, height));
        PositionMetricMarker(AirtimeMarker, x, MapSampleY(sample.Airtime, 0, 100, height));

        SelectionTooltipTime.Text = sample.TimestampLocal.ToString("ddd dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        SelectionTooltipBattery.Text = $"Battery: {sample.BatteryDisplay}";
        SelectionTooltipChannel.Text = $"Channel: {sample.ChannelUtilizationDisplay}";
        SelectionTooltipAirtime.Text = $"Airtime: {sample.AirtimeDisplay}";
        PositionTooltip(x, height);
    }

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
        BatteryMarker.Visibility = Visibility.Collapsed;
        ChannelMarker.Visibility = Visibility.Collapsed;
        AirtimeMarker.Visibility = Visibility.Collapsed;
        SelectionTooltip.Visibility = Visibility.Collapsed;
    }

    private double MapSampleX(DateTime timestamp, double width)
    {
        var usableWidth = Math.Max(1, width - (2 * PlotPadding));
        var seconds = Math.Max(0, (timestamp - _minTimestamp).TotalSeconds);
        var ratio = _totalSeconds <= 0 ? 0 : Math.Min(1, seconds / _totalSeconds);
        return PlotPadding + (usableWidth * ratio);
    }

    private static double? GetBatteryValue(DeviceMetricSample sample)
        => sample.BatteryVolts.HasValue ? sample.BatteryVolts : sample.BatteryPercent;

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
