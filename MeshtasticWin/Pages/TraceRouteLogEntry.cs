using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeshtasticWin.Pages;

public sealed class TraceRouteLogEntry : INotifyPropertyChanged
{
    private static readonly SolidColorBrush ActiveHeaderBrush = new(ColorHelper.FromArgb(255, 79, 195, 247));
    private static readonly SolidColorBrush PassiveHeaderBrush = new(ColorHelper.FromArgb(255, 255, 183, 77));
    private static readonly SolidColorBrush ForwardPathBrush = new(ColorHelper.FromArgb(255, 224, 224, 224));
    private static readonly SolidColorBrush BackPathBrush = new(ColorHelper.FromArgb(255, 129, 199, 132));

    public event PropertyChangedEventHandler? PropertyChanged;

    public TraceRouteLogEntry(
        string rawLine,
        DateTime timestampUtc,
        string headerText,
        string pathText,
        string? routeBackHeaderText,
        string? routeBackPathText,
        string overlayHeaderText,
        string overlayRouteText,
        string? overlayRouteBackText,
        string? overlayMetricsText,
        bool isPassive,
        int hopCount,
        IReadOnlyList<RouteMapPoint> forwardPoints,
        IReadOnlyList<RouteMapPoint> backPoints,
        IReadOnlyList<double?> forwardQualities,
        IReadOnlyList<double?> backQualities,
        bool canViewRoute)
    {
        RawLine = rawLine;
        TimestampUtc = timestampUtc;
        HeaderText = headerText;
        PathText = pathText;
        RouteBackHeaderText = routeBackHeaderText;
        RouteBackPathText = routeBackPathText;
        OverlayHeaderText = overlayHeaderText;
        OverlayRouteText = overlayRouteText;
        OverlayRouteBackText = overlayRouteBackText;
        OverlayMetricsText = overlayMetricsText;
        IsPassive = isPassive;
        HopCount = hopCount;
        ForwardPoints = forwardPoints;
        BackPoints = backPoints;
        ForwardQualities = forwardQualities;
        BackQualities = backQualities;
        CanViewRoute = canViewRoute;
        RouteBackVisibility = string.IsNullOrWhiteSpace(routeBackHeaderText) && string.IsNullOrWhiteSpace(routeBackPathText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MetricsVisibility = string.IsNullOrWhiteSpace(overlayMetricsText) ? Visibility.Collapsed : Visibility.Visible;
        HeaderBrush = isPassive ? PassiveHeaderBrush : ActiveHeaderBrush;
        PathBrush = ForwardPathBrush;
        RouteBackBrush = BackPathBrush;
    }

    public string RawLine { get; }
    public DateTime TimestampUtc { get; }
    public bool IsPassive { get; private set; }
    public int HopCount { get; private set; }
    public IReadOnlyList<RouteMapPoint> ForwardPoints { get; private set; }
    public IReadOnlyList<RouteMapPoint> BackPoints { get; private set; }
    public IReadOnlyList<double?> ForwardQualities { get; private set; }
    public IReadOnlyList<double?> BackQualities { get; private set; }
    public bool CanViewRoute { get; private set; }

    public string HeaderText { get; private set; }
    public string PathText { get; private set; }
    public string? RouteBackHeaderText { get; private set; }
    public string? RouteBackPathText { get; private set; }
    public Visibility RouteBackVisibility { get; private set; }
    public Brush HeaderBrush { get; private set; }
    public Brush PathBrush { get; private set; }
    public Brush RouteBackBrush { get; private set; }

    public string OverlayHeaderText { get; private set; }
    public string OverlayRouteText { get; private set; }
    public string? OverlayRouteBackText { get; private set; }
    public string? OverlayMetricsText { get; private set; }
    public Visibility MetricsVisibility { get; private set; }

    public void UpdateFrom(TraceRouteLogEntry other)
    {
        HeaderText = other.HeaderText;
        PathText = other.PathText;
        RouteBackHeaderText = other.RouteBackHeaderText;
        RouteBackPathText = other.RouteBackPathText;
        OverlayHeaderText = other.OverlayHeaderText;
        OverlayRouteText = other.OverlayRouteText;
        OverlayRouteBackText = other.OverlayRouteBackText;
        OverlayMetricsText = other.OverlayMetricsText;
        IsPassive = other.IsPassive;
        HopCount = other.HopCount;
        ForwardPoints = other.ForwardPoints;
        BackPoints = other.BackPoints;
        ForwardQualities = other.ForwardQualities;
        BackQualities = other.BackQualities;
        CanViewRoute = other.CanViewRoute;
        RouteBackVisibility = string.IsNullOrWhiteSpace(RouteBackHeaderText) && string.IsNullOrWhiteSpace(RouteBackPathText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MetricsVisibility = string.IsNullOrWhiteSpace(OverlayMetricsText) ? Visibility.Collapsed : Visibility.Visible;
        HeaderBrush = IsPassive ? PassiveHeaderBrush : ActiveHeaderBrush;
        PathBrush = ForwardPathBrush;
        RouteBackBrush = BackPathBrush;

        OnChanged(nameof(HeaderText));
        OnChanged(nameof(PathText));
        OnChanged(nameof(RouteBackHeaderText));
        OnChanged(nameof(RouteBackPathText));
        OnChanged(nameof(RouteBackVisibility));
        OnChanged(nameof(HeaderBrush));
        OnChanged(nameof(PathBrush));
        OnChanged(nameof(RouteBackBrush));
        OnChanged(nameof(OverlayHeaderText));
        OnChanged(nameof(OverlayRouteText));
        OnChanged(nameof(OverlayRouteBackText));
        OnChanged(nameof(OverlayMetricsText));
        OnChanged(nameof(MetricsVisibility));
        OnChanged(nameof(IsPassive));
        OnChanged(nameof(HopCount));
        OnChanged(nameof(ForwardPoints));
        OnChanged(nameof(BackPoints));
        OnChanged(nameof(ForwardQualities));
        OnChanged(nameof(BackQualities));
        OnChanged(nameof(CanViewRoute));
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RouteMapPoint(
    [property: System.Text.Json.Serialization.JsonPropertyName("lat")] double Lat,
    [property: System.Text.Json.Serialization.JsonPropertyName("lon")] double Lon,
    [property: System.Text.Json.Serialization.JsonPropertyName("label")] string? Label);
