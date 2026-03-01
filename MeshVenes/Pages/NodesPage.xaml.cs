using AppDataPaths = MeshVenes.Services.AppDataPaths;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeshVenes.Models;
using MeshVenes.Protocol;
using MeshVenes.Services;
using Meshtastic.Protobufs;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using MapGeoPoint = MeshVenes.Models.GeoPoint;

namespace MeshVenes.Pages;

public sealed partial class NodesPage : Page, INotifyPropertyChanged
{
    private const string LastSelectedNodeKey = "NodesLastSelectedNodeIdHex";
    private static readonly Brush DefaultSignalBrush = new SolidColorBrush(Colors.Gray);
    private static readonly Brush SnrExcellentBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 76, 175, 80));
    private static readonly Brush SnrGoodBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 235, 59));
    private static readonly Brush SnrFairBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 167, 38));
    private static readonly Brush SnrPoorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 244, 67, 54));
    private static readonly Brush SnrUnknownBrush = new SolidColorBrush(Colors.Gray);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _hideInactive = true;
    private string _filter = "";
    private SortMode _sortMode = SortMode.LastHeard;
    private readonly DispatcherTimer _throttle = new();
    private readonly DispatcherTimer _filterApplyTimer = new();
    private bool _mapReady;
    private readonly DispatcherTimer _traceRouteTimer = new();
    private int _traceRouteRemainingSeconds;
    private bool _traceRouteCooldownActive;
    private readonly DispatcherTimer _logPollTimer = new();
    private System.Threading.Tasks.Task? _mapInitializationTask;
    private bool _mapEventsAttached;
    private bool _mapConfigured;
    private bool _pendingAutoFitOnLoad = true;
    private string? _mapFolderPath;
    private Uri? _mapUri;
    private readonly HashSet<string> _enabledTrackNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Lat, double Lon)> _lastTrackPointByNode = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastLogWriteByNode = new();
    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastViewedByNode = new();
    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastAppendedByNode = new();
    private readonly Dictionary<string, HashSet<LogKind>> _pendingLogIndicatorsByNode = new();
    private string _activeLogScope = AppDataPaths.ActiveNodeScope;
    private static readonly SolidColorBrush ActiveTabHeaderBrush = new(ColorHelper.FromArgb(255, 79, 195, 247));
    private readonly Brush _inactiveTabHeaderBrush = ResolveDefaultTabHeaderBrush();

    private bool _deviceMetricsTabIndicator;
    private bool _positionTabIndicator;
    private bool _traceRouteTabIndicator;
    private bool _environmentMetricsTabIndicator;
    private bool _powerMetricsTabIndicator;
    private bool _detectionSensorTabIndicator;

    private string _environmentMetricsLogText = "No log entries yet.";
    private string _powerMetricsLogText = "No log entries yet.";
    private string _detectionSensorLogText = "No log entries yet.";

    internal ObservableCollection<PositionLogEntry> PositionLogEntries { get; } = new();
    private PositionLogEntry? _selectedPositionEntry;
    private int _positionLogRetentionDays = 7;
    private readonly ObservableCollection<DeviceMetricSample> _deviceMetricSamples = new();
    private readonly ObservableCollection<EnvironmentMetricSample> _environmentMetricSamples = new();
    private bool _suspendMetricGraphRefresh;
    public ObservableCollection<DeviceMetricSample> DeviceMetricSamples => _deviceMetricSamples;
    public ObservableCollection<EnvironmentMetricSample> EnvironmentMetricSamples => _environmentMetricSamples;
    public ObservableCollection<TraceRouteLogEntry> TraceRouteLogEntries { get; } = new();
    private string? _traceRouteNodeId;
    private TraceRouteLogEntry? _selectedTraceRouteEntry;
    private bool _isPublicKeyVisible;
    private bool _isSyncingRouteScroll;

    private NodeLive? _selected;
    private string? _lastSelectedNodeIdHex;
    public NodeLive? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (!string.IsNullOrWhiteSpace(_selected?.IdHex))
            {
                _lastSelectedNodeIdHex = _selected.IdHex;
                SettingsStore.SetString(LastSelectedNodeKey, _lastSelectedNodeIdHex);
            }
            _isPublicKeyVisible = false;

            OnChanged(nameof(Selected));
            OnChanged(nameof(HasSelection));
            OnChanged(nameof(SelectedTitle));
            OnChanged(nameof(SelectedIdHex));
            OnChanged(nameof(SelectedNodeNumText));
            OnChanged(nameof(SelectedLastHeardText));
            OnChanged(nameof(SelectedPosText));
            OnChanged(nameof(SelectedShortNameText));
            OnChanged(nameof(SelectedUserIdText));
            OnChanged(nameof(SelectedPublicKeyText));
            OnChanged(nameof(SelectedPublicKeyDisplayText));
            OnChanged(nameof(HasSelectedPublicKey));
            OnChanged(nameof(PublicKeyToggleButtonText));
            OnChanged(nameof(SelectedFirmwareVersionText));
            OnChanged(nameof(SelectedRoleText));
            OnChanged(nameof(SelectedUptimeText));
            OnChanged(nameof(SelectedFirstHeardText));
            OnChanged(nameof(SelectedHardwareModelText));
            OnChanged(nameof(SelectedSignalDetailsText));
            OnChanged(nameof(SelectedSignalBrush));
            OnChanged(nameof(HasSelectedPosition));
            OnChanged(nameof(IsTraceRouteEnabled));
            OnChanged(nameof(TraceRouteButtonText));
            OnChanged(nameof(GpsTrackButtonText));
            OnChanged(nameof(IsConnectedNodeSelected));
            OnChanged(nameof(IgnoreDeleteActionsVisibility));
            OnChanged(nameof(ConnectedNodeActionsVisibility));
            OnChanged(nameof(CanEditSelectedNodeState));
            OnChanged(nameof(CanRunConnectedNodeActions));
            OnChanged(nameof(IgnoreNodeButtonText));

            SelectedTraceRouteEntry = null;
            if (TraceRouteDetailsTip is not null)
                TraceRouteDetailsTip.IsOpen = false;

            if (_selected is not null)
            {
                _selected.HasLogIndicator = false;
                ApplyPendingIndicatorsForSelectedNode();
                RefreshSelectedNodeLogs();
                var logKind = TabIndexToLogKind(DetailsTabs.SelectedIndex);
                if (logKind is not null)
                    MarkTabViewed(logKind.Value, _selected.IdHex);
            }

            _ = PushSelectionToMapAsync();
        }
    }

    public bool HasSelection => Selected is not null;
    public bool HasSelectedPosition => Selected?.HasPosition ?? false;
    public bool IsConnectedNodeSelected =>
        Selected is not null &&
        !string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex) &&
        string.Equals(Selected.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase);

    public Visibility IgnoreDeleteActionsVisibility =>
        HasSelection && !IsConnectedNodeSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectedNodeActionsVisibility =>
        IsConnectedNodeSelected ? Visibility.Visible : Visibility.Collapsed;

    public bool CanEditSelectedNodeState =>
        HasSelection &&
        Selected is { NodeNum: > 0 } &&
        NodeIdentity.TryParseNodeNumFromHex(AppState.ConnectedNodeIdHex, out _);

    public bool CanRunConnectedNodeActions =>
        IsConnectedNodeSelected && Selected is { NodeNum: > 0 };

    public string IgnoreNodeButtonText => Selected?.IsIgnored == true ? "Unignore Node" : "Ignore Node";

    public string SelectedTitle => Selected?.Name ?? "Select a node";
    public string SelectedIdHex => Selected?.IdHex ?? "—";
    public string SelectedNodeNumText => Selected is null || Selected.NodeNum == 0 ? "—" : Selected.NodeNum.ToString(CultureInfo.InvariantCulture);
    public string SelectedLastHeardText
    {
        get
        {
            if (Selected is null || Selected.LastHeardUtc == DateTime.MinValue)
                return "—";

            var local = Selected.LastHeardUtc.ToLocalTime();
            var relative = FormatRelativeAge(Selected.LastHeardUtc);
            return $"{local:HH:mm:ss} ({relative})";
        }
    }
    public string SelectedPosText
    {
        get
        {
            if (Selected is null || !Selected.HasPosition)
                return "—";

            var lat = Selected.Latitude.ToString("0.000000", CultureInfo.InvariantCulture);
            var lon = Selected.Longitude.ToString("0.000000", CultureInfo.InvariantCulture);
            var relative = FormatRelativeAge(Selected.LastPositionUtc);
            var local = Selected.LastPositionUtc.ToLocalTime();
            return $"{local:HH:mm:ss} {lat}, {lon} ({relative})";
        }
    }
    public string SelectedShortNameText
    {
        get
        {
            if (Selected is null)
                return "—";

            return string.IsNullOrWhiteSpace(Selected.ShortName) ? "—" : Selected.ShortName;
        }
    }

    public string SelectedUserIdText
    {
        get
        {
            if (Selected is null)
                return "—";

            var name = !string.IsNullOrWhiteSpace(Selected.LongName)
                ? Selected.LongName
                : !string.IsNullOrWhiteSpace(Selected.ShortName)
                    ? Selected.ShortName
                    : "";

            if (!string.IsNullOrWhiteSpace(Selected.UserId))
                return string.IsNullOrWhiteSpace(name) ? Selected.UserId : $"{Selected.UserId} ({name})";

            return string.IsNullOrWhiteSpace(name) ? "—" : name;
        }
    }

    public string SelectedPublicKeyText =>
        Selected is null || string.IsNullOrWhiteSpace(Selected.PublicKey)
            ? "—"
            : Selected.PublicKey;

    public string SelectedPublicKeyDisplayText =>
        !HasSelectedPublicKey
            ? "—"
            : _isPublicKeyVisible
                ? SelectedPublicKeyText
                : "Hidden";

    public bool HasSelectedPublicKey =>
        Selected is not null && !string.IsNullOrWhiteSpace(Selected.PublicKey);

    public string PublicKeyToggleButtonText => _isPublicKeyVisible ? "Hide" : "Show";

    public string SelectedFirmwareVersionText =>
        Selected is null || string.IsNullOrWhiteSpace(Selected.FirmwareVersion)
            ? "—"
            : Selected.FirmwareVersion;

    public string SelectedRoleText =>
        Selected is null || string.IsNullOrWhiteSpace(Selected.Role)
            ? "—"
            : Selected.Role;

    public string SelectedUptimeText
    {
        get
        {
            if (Selected?.UptimeSeconds is not uint uptime || uptime == 0)
                return "—";

            return FormatDuration(TimeSpan.FromSeconds(uptime));
        }
    }

    public string SelectedFirstHeardText
    {
        get
        {
            if (Selected is null || Selected.FirstHeardUtc == DateTime.MinValue)
                return "—";

            var local = Selected.FirstHeardUtc.ToLocalTime();
            var relative = FormatRelativeAge(Selected.FirstHeardUtc);
            return $"{local:yyyy.MM.dd HH:mm:ss} ({relative})";
        }
    }

    public string SelectedHardwareModelText =>
        Selected is null || string.IsNullOrWhiteSpace(Selected.HardwareModel)
            ? "—"
            : Selected.HardwareModel;

    public string SelectedSignalDetailsText =>
        Selected?.SignalDetailsText ?? "—";

    public Brush SelectedSignalBrush =>
        Selected?.TransportBrush ?? DefaultSignalBrush;

    public double PositionLogRetentionDaysValue
    {
        get => _positionLogRetentionDays;
        set
        {
            var next = (int)Math.Max(1, Math.Round(value));
            if (_positionLogRetentionDays == next) return;
            _positionLogRetentionDays = next;
            OnChanged(nameof(PositionLogRetentionDaysValue));
        }
    }

    public bool IsTraceRouteEnabled => HasSelection && !_traceRouteCooldownActive;

    public string GpsTrackButtonText => IsSelectedTrackEnabled ? "Hide GPS track" : "Load GPS track";

    public string TraceRouteButtonText =>
        _traceRouteCooldownActive
            ? $"Trace Route ({_traceRouteRemainingSeconds}s)"
            : "Trace Route";

    public string DeviceMetricsCountText => $"Readings Total: {_deviceMetricSamples.Count}";
    public string EnvironmentMetricsCountText => $"Readings Total: {_environmentMetricSamples.Count}";
    public string PositionLogCountText => $"Readings Total: {PositionLogEntries.Count}";
    public string TraceRouteLogCountText => $"Readings Total: {TraceRouteLogEntries.Count}";

    public TraceRouteLogEntry? SelectedTraceRouteEntry
    {
        get => _selectedTraceRouteEntry;
        private set
        {
            if (_selectedTraceRouteEntry == value) return;
            _selectedTraceRouteEntry = value;
            OnChanged(nameof(SelectedTraceRouteEntry));
            OnChanged(nameof(TraceRouteDetailTitle));
            OnChanged(nameof(TraceRouteDetailHeader));
            OnChanged(nameof(TraceRouteDetailRoute));
            OnChanged(nameof(TraceRouteDetailRouteBack));
            OnChanged(nameof(TraceRouteDetailRouteBackVisibility));
            OnChanged(nameof(TraceRouteDetailMetrics));
            OnChanged(nameof(TraceRouteDetailMetricsVisibility));
            OnChanged(nameof(TraceRouteDetailBackMetrics));
            OnChanged(nameof(TraceRouteDetailBackMetricsVisibility));
            OnChanged(nameof(CanViewTraceRouteMap));
        }
    }

    public string PowerMetricsLogText
    {
        get => _powerMetricsLogText;
        private set
        {
            if (_powerMetricsLogText == value) return;
            _powerMetricsLogText = value;
            OnChanged(nameof(PowerMetricsLogText));
        }
    }

    public string EnvironmentMetricsLogText
    {
        get => _environmentMetricsLogText;
        private set
        {
            if (_environmentMetricsLogText == value) return;
            _environmentMetricsLogText = value;
            OnChanged(nameof(EnvironmentMetricsLogText));
        }
    }

    public string DetectionSensorLogText
    {
        get => _detectionSensorLogText;
        private set
        {
            if (_detectionSensorLogText == value) return;
            _detectionSensorLogText = value;
            OnChanged(nameof(DetectionSensorLogText));
        }
    }

    public bool HasPositionSelection => _selectedPositionEntry is not null;

    public Visibility DeviceMetricsTabIndicatorVisibility => _deviceMetricsTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PositionTabIndicatorVisibility => _positionTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TraceRouteTabIndicatorVisibility => _traceRouteTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EnvironmentMetricsTabIndicatorVisibility => _environmentMetricsTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PowerMetricsTabIndicatorVisibility => _powerMetricsTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetectionSensorTabIndicatorVisibility => _detectionSensorTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PowerMetricsTabVisibility => AppState.ShowPowerMetricsTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetectionSensorTabVisibility => AppState.ShowDetectionSensorLogTab ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TraceRouteEmptyVisibility => TraceRouteLogEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string TraceRouteDetailTitle => SelectedTraceRouteEntry is null
        ? "Trace Route Details"
        : SelectedTraceRouteEntry.IsPassive ? "Passive Trace Route" : "Active Trace Route";

    public string TraceRouteDetailHeader => SelectedTraceRouteEntry?.OverlayHeaderText ?? "";
    public string TraceRouteDetailRoute => SelectedTraceRouteEntry?.OverlayRouteText ?? "";
    public string TraceRouteDetailRouteBack => SelectedTraceRouteEntry?.OverlayRouteBackText ?? "";
    public Visibility TraceRouteDetailRouteBackVisibility =>
        string.IsNullOrWhiteSpace(SelectedTraceRouteEntry?.OverlayRouteBackText) ? Visibility.Collapsed : Visibility.Visible;
    public string TraceRouteDetailMetrics => SelectedTraceRouteEntry?.OverlayMetricsText ?? "";
    public Visibility TraceRouteDetailMetricsVisibility => SelectedTraceRouteEntry?.MetricsVisibility ?? Visibility.Collapsed;
    public string TraceRouteDetailBackMetrics => SelectedTraceRouteEntry?.RouteBackPathText ?? "";
    public Visibility TraceRouteDetailBackMetricsVisibility => SelectedTraceRouteEntry?.RouteBackDetailVisibility ?? Visibility.Collapsed;
    public bool CanViewTraceRouteMap => SelectedTraceRouteEntry?.CanViewRoute ?? false;

    public string NodeCountsText
    {
        get
        {
            var total = MeshVenes.AppState.Nodes.Count;
            var online = MeshVenes.AppState.Nodes.Count(n => IsOnlineByRssi(n));
            return $"Online: {online}   Total: {total}";
        }
    }

    public ObservableCollection<NodeLive> NodesSource => MeshVenes.AppState.Nodes;
    public ObservableCollection<NodeLive> VisibleNodes { get; } = new();
    private readonly ObservableCollection<NodeLive> _allNodes = new();
    private readonly DispatcherTimer _nodeSortRefreshTimer = new();

    private enum SortMode
    {
        Alphabetical,
        HopsAway,
        LastHeard,
        FavoritesFirst
    }

    public NodesPage()
    {
        InitializeComponent();
        _lastSelectedNodeIdHex = LoadLastSelectedNodeIdHex();

        _deviceMetricSamples.CollectionChanged += DeviceMetricSamples_CollectionChanged;
        _environmentMetricSamples.CollectionChanged += EnvironmentMetricSamples_CollectionChanged;
        PositionLogEntries.CollectionChanged += (_, __) => OnChanged(nameof(PositionLogCountText));
        TraceRouteLogEntries.CollectionChanged += (_, __) =>
        {
            OnChanged(nameof(TraceRouteEmptyVisibility));
            OnChanged(nameof(TraceRouteLogCountText));
        };

        SortCombo.Items.Add("Sort: Alphabetical");
        SortCombo.Items.Add("Sort: Hops away");
        SortCombo.Items.Add("Sort: Last heard");
        SortCombo.Items.Add("Sort: Favorites first");
        SortCombo.SelectedIndex = 2;

        HideInactiveToggle.IsChecked = _hideInactive;
        NodesView.Source = VisibleNodes;
        ApplyNodeSorting();

        MeshVenes.AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        foreach (var n in MeshVenes.AppState.Nodes)
        {
            n.PropertyChanged += Node_PropertyChanged;
            _allNodes.Add(n);
        }
        MeshVenes.AppState.Waypoints.CollectionChanged += Waypoints_CollectionChanged;
        foreach (var waypoint in MeshVenes.AppState.Waypoints)
            waypoint.PropertyChanged += Waypoint_PropertyChanged;
        AppState.ConnectedNodeChanged += ConnectedNodeChanged;
        AppState.SettingsChanged += AppState_SettingsChanged;

        RebuildVisibleNodes();
        RefreshNodeDistanceAndDirection();
        EnsureSelectedTabVisible();
        UpdateTabHeaderColors();

        _filterApplyTimer.Interval = TimeSpan.FromMilliseconds(250);
        _filterApplyTimer.Tick += (_, __) =>
        {
            _filterApplyTimer.Stop();
            RebuildVisibleNodes();
        };

        _nodeSortRefreshTimer.Interval = TimeSpan.FromMilliseconds(300);
        _nodeSortRefreshTimer.Tick += (_, __) =>
        {
            _nodeSortRefreshTimer.Stop();
            ApplyNodeSorting();
        };

        _throttle.Interval = TimeSpan.FromMilliseconds(350);
        _throttle.Tick += (_, __) =>
        {
            _throttle.Stop();
            _ = PushAllNodesToMapAsync();
            _ = PushWaypointsToMapAsync();
        };

        _traceRouteTimer.Interval = TimeSpan.FromSeconds(1);
        _traceRouteTimer.Tick += (_, __) => TraceRouteCooldownTick();

        _logPollTimer.Interval = TimeSpan.FromSeconds(2);
        _logPollTimer.Tick += (_, __) => PollLogs();

        Loaded += NodesPage_Loaded;
        Unloaded += NodesPage_Unloaded;
    }

    private void NodesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MeshVenes.AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        foreach (var n in MeshVenes.AppState.Nodes)
            n.PropertyChanged -= Node_PropertyChanged;
        MeshVenes.AppState.Waypoints.CollectionChanged -= Waypoints_CollectionChanged;
        foreach (var waypoint in MeshVenes.AppState.Waypoints)
            waypoint.PropertyChanged -= Waypoint_PropertyChanged;

        DeviceMetricsLogService.SampleAdded -= DeviceMetricsLogService_SampleAdded;
        AppState.ConnectedNodeChanged -= ConnectedNodeChanged;
        AppState.SettingsChanged -= AppState_SettingsChanged;
        _filterApplyTimer.Stop();
        _logPollTimer.Stop();
    }

    private static Brush ResolveDefaultTabHeaderBrush()
    {
        if (Application.Current?.Resources is ResourceDictionary resources &&
            resources.TryGetValue("TextFillColorPrimaryBrush", out var brushObj) &&
            brushObj is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.White);
    }

    private void AppState_SettingsChanged()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            OnChanged(nameof(PowerMetricsTabVisibility));
            OnChanged(nameof(DetectionSensorTabVisibility));
            EnsureSelectedTabVisible();
            UpdateTabHeaderColors();
        });
    }

    private void EnsureSelectedTabVisible()
    {
        var selected = DetailsTabs.SelectedItem as TabViewItem;

        if (!AppState.ShowPowerMetricsTab && selected == PowerMetricsTabItem)
            DetailsTabs.SelectedItem = MapTabItem;

        if (!AppState.ShowDetectionSensorLogTab && selected == DetectionSensorTabItem)
            DetailsTabs.SelectedItem = MapTabItem;
    }

    private void UpdateTabHeaderColors()
    {
        var selected = DetailsTabs.SelectedItem as TabViewItem;
        SetTabHeaderBrush(MapTabHeaderText, selected == MapTabItem);
        SetTabHeaderBrush(DeviceMetricsTabHeaderText, selected == DeviceMetricsTabItem);
        SetTabHeaderBrush(PositionLogTabHeaderText, selected == PositionLogTabItem);
        SetTabHeaderBrush(TraceRouteTabHeaderText, selected == TraceRouteTabItem);
        SetTabHeaderBrush(EnvironmentMetricsTabHeaderText, selected == EnvironmentMetricsTabItem);
        SetTabHeaderBrush(PowerMetricsTabHeaderText, selected == PowerMetricsTabItem);
        SetTabHeaderBrush(DetectionSensorTabHeaderText, selected == DetectionSensorTabItem);
    }

    private void SetTabHeaderBrush(TextBlock headerText, bool isActive)
    {
        headerText.Foreground = isActive ? ActiveTabHeaderBrush : _inactiveTabHeaderBrush;
    }

    private async void NodesPage_Loaded(object sender, RoutedEventArgs e)
    {
        _pendingAutoFitOnLoad = true;
        EnsureSelectedTabVisible();
        UpdateTabHeaderColors();
        await EnsureMapAsync();
        await PushAllNodesToMapAsync();
        await PushSelectionToMapAsync();
        TryAutoFitMapOnLoad();
        SeedLogWriteTimes();
        DeviceMetricsLogService.SampleAdded += DeviceMetricsLogService_SampleAdded;
        _logPollTimer.Start();
    }

    private async System.Threading.Tasks.Task EnsureMapAsync()
    {
        if (_mapConfigured)
            return;

        if (_mapInitializationTask is null)
            _mapInitializationTask = InitializeMapAsync();

        await _mapInitializationTask;
    }

    private async System.Threading.Tasks.Task InitializeMapAsync()
    {
        if (_mapConfigured)
            return;

        try
        {
            await MapView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Map initialization failed: {ex.Message}");
            ShowMapFallback("Map initialization failed.");
            _mapInitializationTask = null;
            return;
        }

        var wv = MapView.CoreWebView2;
        if (wv is null)
        {
            ShowMapFallback("Map initialization failed.");
            _mapInitializationTask = null;
            return;
        }

        if (!_mapEventsAttached)
        {
            wv.WebMessageReceived += CoreWebView2_WebMessageReceived;
            wv.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _mapEventsAttached = true;
        }

        var installPath = ResolveInstallPath();
        _mapFolderPath = Path.GetFullPath(Path.Combine(installPath, "Assets"));

        if (!Directory.Exists(_mapFolderPath))
        {
            RadioClient.Instance.AddLogFromUiThread($"Map assets missing: {_mapFolderPath}");
            ShowMapFallback("Map assets missing.");
            _mapInitializationTask = null;
            return;
        }

        var mapHtml = Path.Combine(_mapFolderPath, "Map", "map.html");
        if (!File.Exists(mapHtml))
        {
            RadioClient.Instance.AddLogFromUiThread($"Map HTML missing: {mapHtml}");
            ShowMapFallback("Map HTML missing.");
            _mapInitializationTask = null;
            return;
        }

        _mapUri = new Uri("https://appassets.local/Map/map.html");

        try
        {
            _mapReady = false;
            wv.SetVirtualHostNameToFolderMapping("appassets.local", _mapFolderPath, CoreWebView2HostResourceAccessKind.Allow);
            MapView.Source = _mapUri;
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Map navigation setup failed: {ex.Message}");
            ShowMapFallback("Map navigation setup failed.");
            _mapInitializationTask = null;
            return;
        }

        HideMapFallback();
        _mapConfigured = true;
    }

    private static string ResolveInstallPath()
    {
        if (Packaging.IsPackaged())
        {
            try
            {
                return Package.Current.InstalledLocation.Path;
            }
            catch
            {
                return TrimBaseDirectory();
            }
        }

        return TrimBaseDirectory();
    }

    private static string TrimBaseDirectory()
    {
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(type)) return;

            if (type == "ready")
            {
                _mapReady = true;
                _ = PushAllNodesToMapAsync();
                _ = PushWaypointsToMapAsync();
                _ = PushSelectionToMapAsync();
                _ = PushEnabledTracksToMapAsync();
                TryAutoFitMapOnLoad();
                return;
            }

            if (type == "selectNode")
            {
                var id = root.TryGetProperty("idHex", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) return;

                var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.IdHex.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (node is not null)
                {
                    NodesList.SelectedItem = node;
                    Selected = node;

                    // Scroll to make the selected node visible.
                    NodesList.ScrollIntoView(node);
                }
                return;
            }

            if (type == "requestHistory")
            {
                var id = root.TryGetProperty("idHex", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) return;

                if (!GpsArchive.HasLog(id)) return;

                var points = GpsArchive.ReadAll(id, maxPoints: 5000);
                var payload = new
                {
                    type = "history",
                    idHex = id,
                    points = points.Select(p => new GeoPointWithTimestamp(
                        p.Lat,
                        p.Lon,
                        p.TsUtc.ToString("o", CultureInfo.InvariantCulture)))
                };
                MapView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
                return;
            }

            if (type == "waypointUpsert")
            {
                if (TryParseWaypointUpsertRequest(root, out var request))
                    _ = HandleWaypointUpsertRequestAsync(request);
                return;
            }

            if (type == "waypointDelete")
            {
                if (TryParseWaypointDeleteRequest(root, out var waypointId))
                    _ = HandleWaypointDeleteRequestAsync(waypointId);
            }
        }
        catch { }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var uri = MapView.Source?.ToString() ?? MapView.CoreWebView2?.Source ?? "unknown";
        if (e.IsSuccess)
        {
            HideMapFallback();
            _ = MapView.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            return;
        }

        _mapReady = false;
        RadioClient.Instance.AddLogFromUiThread($"Map navigation failed: {e.WebErrorStatus} ({uri})");
        ShowMapFallback($"Map failed to load: {e.WebErrorStatus}");
        _mapConfigured = false;
        _mapInitializationTask = null;
    }

    private sealed record WaypointUpsertRequest(
        uint WaypointId,
        double Latitude,
        double Longitude,
        string Name,
        string Description,
        string IconGlyph,
        uint LockedToNodeNum,
        long? ExpireUnixUtc,
        uint ChannelIndex);

    private static bool TryParseWaypointUpsertRequest(JsonElement root, out WaypointUpsertRequest request)
    {
        request = new WaypointUpsertRequest(0, 0, 0, "", "", "", 0, null, 0);

        if (!root.TryGetProperty("waypoint", out var waypointEl) || waypointEl.ValueKind != JsonValueKind.Object)
            return false;

        uint waypointId = 0;
        _ = TryReadUInt(waypointEl, "id", out waypointId);

        if (!TryReadDouble(waypointEl, "lat", out var lat) || !TryReadDouble(waypointEl, "lon", out var lon))
            return false;

        var name = TryReadString(waypointEl, "name");
        var description = TryReadString(waypointEl, "description");
        var iconGlyph = TryReadString(waypointEl, "iconGlyph");

        uint lockedTo = 0;
        _ = TryReadUInt(waypointEl, "lockedTo", out lockedTo);

        long? expireUnix = null;
        if (TryReadLong(waypointEl, "expireUnix", out var parsedExpire) && parsedExpire > 0)
            expireUnix = parsedExpire;

        uint channel = 0;
        _ = TryReadUInt(waypointEl, "channel", out channel);

        request = new WaypointUpsertRequest(waypointId, lat, lon, name, description, iconGlyph, lockedTo, expireUnix, channel);
        return true;
    }

    private static bool TryParseWaypointDeleteRequest(JsonElement root, out uint waypointId)
    {
        waypointId = 0;
        return TryReadUInt(root, "id", out waypointId) && waypointId != 0;
    }

    private async System.Threading.Tasks.Task HandleWaypointUpsertRequestAsync(WaypointUpsertRequest request)
    {
        if (!IsValidMapPosition(request.Latitude, request.Longitude))
            return;

        if (!RadioClient.Instance.IsConnected)
        {
            RadioClient.Instance.AddLogFromUiThread("Waypoint send skipped: radio is not connected.");
            return;
        }

        var waypointId = request.WaypointId != 0 ? request.WaypointId : PacketIdGenerator.Next();
        var expireUtc = request.ExpireUnixUtc.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(request.ExpireUnixUtc.Value).UtcDateTime
            : (DateTime?)null;

        var payload = new Waypoint
        {
            Id = waypointId,
            LatitudeI = (int)Math.Round(request.Latitude * 1e7),
            LongitudeI = (int)Math.Round(request.Longitude * 1e7),
            Name = LimitText(string.IsNullOrWhiteSpace(request.Name) ? $"Waypoint {waypointId:x8}" : request.Name.Trim(), 30),
            Description = LimitText(request.Description?.Trim() ?? "", 100),
            LockedTo = request.LockedToNodeNum,
            Icon = WaypointLive.ParseCodepointFromGlyph(request.IconGlyph)
        };

        if (request.ExpireUnixUtc.HasValue && request.ExpireUnixUtc.Value > 0)
            payload.Expire = (uint)Math.Clamp(request.ExpireUnixUtc.Value, 1L, uint.MaxValue);

        var sentPacketId = await RadioClient.Instance.SendWaypointAsync(payload, channel: request.ChannelIndex);
        if (sentPacketId == 0)
        {
            RadioClient.Instance.AddLogFromUiThread($"Waypoint send failed: {payload.Name}");
            return;
        }

        var sourceNodeNum = Selected is { NodeNum: > 0 } ? (uint)Selected.NodeNum : 0u;
        var sourceIdHex = sourceNodeNum > 0 ? $"0x{sourceNodeNum:x8}" : (AppState.ConnectedNodeIdHex ?? "");
        var live = new WaypointLive(waypointId)
        {
            SourceNodeNum = sourceNodeNum,
            SourceIdHex = sourceIdHex,
            Name = payload.Name,
            Description = payload.Description,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            IconCodepoint = payload.Icon,
            LockedToNodeNum = payload.LockedTo,
            ExpireUtc = expireUtc,
            LastUpdatedUtc = DateTime.UtcNow,
            ChannelIndex = request.ChannelIndex
        };

        AppState.UpsertWaypoint(live);
        await PushWaypointsToMapAsync();
        RadioClient.Instance.AddLogFromUiThread($"Waypoint sent: {live.DisplayName} (0x{waypointId:x8})");
    }

    private async System.Threading.Tasks.Task HandleWaypointDeleteRequestAsync(uint waypointId)
    {
        if (waypointId == 0)
            return;

        if (!RadioClient.Instance.IsConnected)
        {
            RadioClient.Instance.AddLogFromUiThread("Waypoint delete skipped: radio is not connected.");
            return;
        }

        var existing = AppState.FindWaypoint(waypointId);
        var deletePayload = new Waypoint
        {
            Id = waypointId,
            Expire = (uint)Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1)
        };

        if (existing is not null)
        {
            deletePayload.Name = LimitText(existing.Name, 30);
            deletePayload.Description = LimitText(existing.Description, 100);
            deletePayload.Icon = existing.IconCodepoint;
            deletePayload.LockedTo = existing.LockedToNodeNum;
            if (existing.HasPosition)
            {
                deletePayload.LatitudeI = (int)Math.Round(existing.Latitude * 1e7);
                deletePayload.LongitudeI = (int)Math.Round(existing.Longitude * 1e7);
            }
        }

        var channel = existing?.ChannelIndex ?? 0u;
        var sentPacketId = await RadioClient.Instance.SendWaypointAsync(deletePayload, channel: channel);
        if (sentPacketId == 0)
        {
            RadioClient.Instance.AddLogFromUiThread($"Waypoint delete failed: 0x{waypointId:x8}");
            return;
        }

        AppState.RemoveWaypoint(waypointId);
        await PushWaypointsToMapAsync();
        RadioClient.Instance.AddLogFromUiThread($"Waypoint deleted: 0x{waypointId:x8}");
    }

    private static string LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
            return "";

        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string TryReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return "";

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";

        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return value.ToString();

        return "";
    }

    private static bool TryReadUInt(JsonElement root, string propertyName, out uint value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetUInt32(out value);

        if (element.ValueKind == JsonValueKind.String &&
            uint.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadLong(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out value);

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out value);

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private void ShowMapFallback(string message)
    {
        MapFallbackText.Text = message;
        MapFallbackText.Visibility = Visibility.Visible;
    }

    private void HideMapFallback()
    {
        MapFallbackText.Visibility = Visibility.Collapsed;
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!EnsureOnUi(() => Nodes_CollectionChanged(sender, e)))
            return;

        if (e.NewItems is not null)
            foreach (NodeLive n in e.NewItems)
            {
                n.PropertyChanged += Node_PropertyChanged;
                SeedLogWriteTimesForNode(n);
                _allNodes.Add(n);
            }

        if (e.OldItems is not null)
            foreach (NodeLive n in e.OldItems)
            {
                n.PropertyChanged -= Node_PropertyChanged;
                _allNodes.Remove(n);
            }

        ScheduleFilterApply();
        OnChanged(nameof(NodeCountsText));
        RefreshNodeDistanceAndDirection();
        TriggerMapUpdate();
    }

    private void Waypoints_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!EnsureOnUi(() => Waypoints_CollectionChanged(sender, e)))
            return;

        if (e.NewItems is not null)
        {
            foreach (WaypointLive waypoint in e.NewItems)
                waypoint.PropertyChanged += Waypoint_PropertyChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (WaypointLive waypoint in e.OldItems)
                waypoint.PropertyChanged -= Waypoint_PropertyChanged;
        }

        TriggerMapUpdate();
    }

    private void Waypoint_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!EnsureOnUi(() => Waypoint_PropertyChanged(sender, e)))
            return;

        if (e.PropertyName is nameof(WaypointLive.Latitude)
            or nameof(WaypointLive.Longitude)
            or nameof(WaypointLive.Name)
            or nameof(WaypointLive.Description)
            or nameof(WaypointLive.IconCodepoint)
            or nameof(WaypointLive.ExpireUtc)
            or nameof(WaypointLive.LockedToNodeNum))
        {
            TriggerMapUpdate();
        }
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!EnsureOnUi(() => Node_PropertyChanged(sender, e)))
            return;

        if (ReferenceEquals(sender, Selected))
        {
            OnChanged(nameof(SelectedLastHeardText));
            OnChanged(nameof(SelectedPosText));
            OnChanged(nameof(SelectedShortNameText));
            OnChanged(nameof(SelectedNodeNumText));
            OnChanged(nameof(SelectedUserIdText));
            OnChanged(nameof(SelectedPublicKeyText));
            OnChanged(nameof(SelectedPublicKeyDisplayText));
            OnChanged(nameof(HasSelectedPublicKey));
            OnChanged(nameof(PublicKeyToggleButtonText));
            OnChanged(nameof(SelectedFirmwareVersionText));
            OnChanged(nameof(SelectedRoleText));
            OnChanged(nameof(SelectedUptimeText));
            OnChanged(nameof(SelectedFirstHeardText));
            OnChanged(nameof(SelectedHardwareModelText));
            OnChanged(nameof(SelectedSignalDetailsText));
            OnChanged(nameof(SelectedSignalBrush));
            OnChanged(nameof(HasSelectedPosition));
            OnChanged(nameof(SelectedTitle));
            OnChanged(nameof(IgnoreNodeButtonText));
            OnChanged(nameof(CanEditSelectedNodeState));
            OnChanged(nameof(CanRunConnectedNodeActions));
        }

        if (e.PropertyName is nameof(NodeLive.LastHeardUtc) or nameof(NodeLive.LastHeard)
            or nameof(NodeLive.Latitude) or nameof(NodeLive.Longitude) or nameof(NodeLive.LastPositionUtc)
            or nameof(NodeLive.RSSI) or nameof(NodeLive.SNR) or nameof(NodeLive.ViaMqtt)
            or nameof(NodeLive.Name) or nameof(NodeLive.ShortName) or nameof(NodeLive.SortNameKey)
            or nameof(NodeLive.IsFavorite) or nameof(NodeLive.NodeNum) or nameof(NodeLive.IsIgnored)
            or nameof(NodeLive.HopsAway))
        {
            OnChanged(nameof(NodeCountsText));
            ScheduleFilterApply();
            TriggerMapUpdate();
            RefreshNodeSorting();
        }

        if (sender is NodeLive updatedNode
            && (e.PropertyName is nameof(NodeLive.Latitude) or nameof(NodeLive.Longitude) or nameof(NodeLive.LastPositionUtc)))
        {
            RefreshNodeDistanceAndDirection();
            TryAppendTrackPoint(updatedNode);
        }
    }

    private void TogglePublicKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelectedPublicKey)
            return;

        _isPublicKeyVisible = !_isPublicKeyVisible;
        OnChanged(nameof(SelectedPublicKeyDisplayText));
        OnChanged(nameof(PublicKeyToggleButtonText));
    }

    private void CopySelectedPublicKey_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSelectedPublicKey || Selected is null)
            return;

        CopyTextToClipboard(Selected.PublicKey);
    }

    private async void NodeFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not NodeLive node)
            return;

        if (node.NodeNum == 0 || node.NodeNum > uint.MaxValue)
        {
            await ShowStatusAsync("Favorite update failed: node number is missing.");
            return;
        }

        if (!NodeIdentity.TryParseNodeNumFromHex(AppState.ConnectedNodeIdHex, out var adminNodeNum))
        {
            await ShowStatusAsync("Favorite update failed: no connected node available.");
            return;
        }

        var desiredFavorite = !node.IsFavorite;
        if (element is Control control)
            control.IsEnabled = false;

        try
        {
            await AdminConfigClient.Instance.SetFavoriteNodeAsync(adminNodeNum, (uint)node.NodeNum, desiredFavorite);
            node.IsFavorite = desiredFavorite;
            RebuildVisibleNodes();
            RadioClient.Instance.AddLogFromUiThread(
                desiredFavorite
                    ? $"Favorite set for node {node.Name} (0x{((uint)node.NodeNum):x8})."
                    : $"Favorite removed for node {node.Name} (0x{((uint)node.NodeNum):x8}).");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Favorite update failed: {ex.Message}");
        }
        finally
        {
            if (element is Control c)
                c.IsEnabled = true;
        }
    }

    private void TextCopyFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        var text = (flyout.Target as TextBlock)?.Text;
        var isEnabled = !string.IsNullOrWhiteSpace(text);
        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
        {
            item.IsEnabled = isEnabled;
            item.CommandParameter = text;
        }
    }

    private void CopyTextBlockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
            return;

        var text = item.CommandParameter as string;
        CopyTextToClipboard(text);
    }

    private static void CopyTextToClipboard(string? text)
    {
        _ = ClipboardUtil.TrySetText(text);
    }

    private static bool IsOnlineByRssi(NodeLive n)
    {
        // Online when RSSI is measured (not "—" and not 0).
        if (string.IsNullOrWhiteSpace(n.RSSI) || n.RSSI == "—") return false;
        if (int.TryParse(n.RSSI, out var rssi))
            return rssi != 0;
        return false;
    }

    private void RefreshNodeDistanceAndDirection()
    {
        var connectedIdHex = AppState.ConnectedNodeIdHex;
        NodeLive? connectedNode = null;

        if (!string.IsNullOrWhiteSpace(connectedIdHex))
        {
            connectedNode = MeshVenes.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, connectedIdHex, StringComparison.OrdinalIgnoreCase));
        }

        var hasConnectedPosition = connectedNode is not null
            && connectedNode.HasPosition
            && IsValidMapPosition(connectedNode.Latitude, connectedNode.Longitude);

        foreach (var node in MeshVenes.AppState.Nodes)
        {
            if (!hasConnectedPosition ||
                !node.HasPosition ||
                !IsValidMapPosition(node.Latitude, node.Longitude))
            {
                node.SetDistanceAndBearing(null, null);
                continue;
            }

            if (connectedNode is not null &&
                string.Equals(node.IdHex, connectedNode.IdHex, StringComparison.OrdinalIgnoreCase))
            {
                node.SetDistanceAndBearing(0, null);
                continue;
            }

            var distanceKm = CalculateDistanceKm(
                connectedNode!.Latitude,
                connectedNode.Longitude,
                node.Latitude,
                node.Longitude);

            var bearingDegrees = CalculateBearingDegrees(
                connectedNode.Latitude,
                connectedNode.Longitude,
                node.Latitude,
                node.Longitude);

            node.SetDistanceAndBearing(distanceKm, bearingDegrees);
        }
    }

    private static double CalculateDistanceKm(double fromLat, double fromLon, double toLat, double toLon)
    {
        const double earthRadiusKm = 6371.0;
        var latDelta = DegreesToRadians(toLat - fromLat);
        var lonDelta = DegreesToRadians(toLon - fromLon);

        var a = Math.Sin(latDelta / 2) * Math.Sin(latDelta / 2) +
                Math.Cos(DegreesToRadians(fromLat)) * Math.Cos(DegreesToRadians(toLat)) *
                Math.Sin(lonDelta / 2) * Math.Sin(lonDelta / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static int CalculateBearingDegrees(double fromLat, double fromLon, double toLat, double toLon)
    {
        var fromLatRad = DegreesToRadians(fromLat);
        var toLatRad = DegreesToRadians(toLat);
        var lonDeltaRad = DegreesToRadians(toLon - fromLon);

        var y = Math.Sin(lonDeltaRad) * Math.Cos(toLatRad);
        var x = Math.Cos(fromLatRad) * Math.Sin(toLatRad) -
                Math.Sin(fromLatRad) * Math.Cos(toLatRad) * Math.Cos(lonDeltaRad);

        var bearingRad = Math.Atan2(y, x);
        var bearing = (RadiansToDegrees(bearingRad) + 360.0) % 360.0;
        return (int)Math.Round(bearing);
    }

    private static double DegreesToRadians(double value)
        => value * Math.PI / 180.0;

    private static double RadiansToDegrees(double value)
        => value * 180.0 / Math.PI;

    private bool IsHiddenByInactive(NodeLive n)
    {
        if (n.IsFavorite) return false;
        if (!_hideInactive) return false;
        if (n.LastHeardUtc == DateTime.MinValue) return true;
        return !IsOnlineByRssi(n);
    }

    private void TriggerMapUpdate()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        _throttle.Stop();
        _throttle.Start();
    }

    private void ScheduleFilterApply()
    {
        if (_filterApplyTimer.IsEnabled)
            _filterApplyTimer.Stop();
        _filterApplyTimer.Start();
    }

    private void RebuildVisibleNodes()
    {
        _filterApplyTimer.Stop();

        EnsureConnectedNodeInNodes();

        var desired = new List<NodeLive>();
        foreach (var node in _allNodes)
        {
            if (ShouldShowNode(node))
                desired.Add(node);
        }

        desired = SortNodes(desired);
        desired = PinConnectedNodeToTop(desired);

        for (var i = VisibleNodes.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(VisibleNodes[i]))
                VisibleNodes.RemoveAt(i);
        }

        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var node = desired[targetIndex];
            if (targetIndex < VisibleNodes.Count && ReferenceEquals(VisibleNodes[targetIndex], node))
                continue;

            var existingIndex = VisibleNodes.IndexOf(node);
            if (existingIndex >= 0)
                VisibleNodes.Move(existingIndex, targetIndex);
            else
                VisibleNodes.Insert(targetIndex, node);
        }

        EnsureSelectionVisible();
        OnChanged(nameof(NodeCountsText));
        RefreshNodeSorting();
#if DEBUG
        Debug.WriteLine($"Nodes filter: all={_allNodes.Count} visible={VisibleNodes.Count} hideInactive={_hideInactive} filter=\"{_filter}\"");
#endif
    }

    private bool ShouldShowNode(NodeLive node)
    {
        if (!string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex) &&
            string.Equals(node.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsHiddenByInactive(node))
            return false;

        var q = (_filter ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return (node.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.IdHex?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.ShortId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void EnsureSelectionVisible()
    {
        if (Selected is not null && VisibleNodes.Contains(Selected))
            return;

        NodeLive? preferred = null;
        if (!string.IsNullOrWhiteSpace(_lastSelectedNodeIdHex))
        {
            preferred = VisibleNodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, _lastSelectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
        }

        var next = preferred ?? VisibleNodes.FirstOrDefault();
        NodesList.SelectedItem = next;
        Selected = next;
    }

    private static string? LoadLastSelectedNodeIdHex()
    {
        try
        {
            var value = SettingsStore.GetString(LastSelectedNodeKey);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        RebuildVisibleNodes();
        TriggerMapUpdate();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortMode = SortCombo.SelectedIndex switch
        {
            1 => SortMode.HopsAway,
            2 => SortMode.LastHeard,
            3 => SortMode.FavoritesFirst,
            _ => SortMode.Alphabetical
        };

        ApplyNodeSorting();
        RebuildVisibleNodes();
        TriggerMapUpdate();
    }

    private void ApplyNodeSorting()
    {
        if (VisibleNodes.Count <= 1)
            return;

        var indexed = VisibleNodes.Select((item, index) => (item, index));
        var sorted = _sortMode switch
        {
            SortMode.LastHeard => indexed
                .OrderByDescending(entry => entry.item.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(entry => entry.item.LastHeardUtc)
                .ThenBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList(),
            SortMode.HopsAway => indexed
                .OrderBy(entry => entry.item.HopsAway ?? int.MaxValue)
                .ThenByDescending(entry => entry.item.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(entry => entry.item.LastHeardUtc)
                .ThenBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList(),
            SortMode.FavoritesFirst => indexed
                .OrderByDescending(entry => entry.item.IsFavorite)
                .ThenByDescending(entry => entry.item.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(entry => entry.item.LastHeardUtc)
                .ThenBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList(),
            _ => indexed
                .OrderBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList()
        };

        ApplySortedOrder(VisibleNodes, sorted);
        PinConnectedNodeToTop();
    }

    private void PinConnectedNodeToTop()
    {
        if (string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
            return;

        var connected = VisibleNodes.FirstOrDefault(n =>
            string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
        if (connected is null)
            return;

        var index = VisibleNodes.IndexOf(connected);
        if (index > 0)
            VisibleNodes.Move(index, 0);
    }

    private List<NodeLive> PinConnectedNodeToTop(List<NodeLive> nodes)
    {
        if (string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
            return nodes;

        var index = nodes.FindIndex(n =>
            string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
        if (index <= 0)
            return nodes;

        var connected = nodes[index];
        nodes.RemoveAt(index);
        nodes.Insert(0, connected);
        return nodes;
    }

    private void RefreshNodeSorting()
    {
        if (!EnsureOnUi(RefreshNodeSorting))
            return;

        if (_nodeSortRefreshTimer.IsEnabled)
            _nodeSortRefreshTimer.Stop();
        _nodeSortRefreshTimer.Start();
    }

    private static void ApplySortedOrder<T>(ObservableCollection<T> collection, IList<T> desiredOrder)
    {
        for (var targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
        {
            var item = desiredOrder[targetIndex];
            var currentIndex = collection.IndexOf(item);
            if (currentIndex < 0 || currentIndex == targetIndex)
                continue;

            collection.Move(currentIndex, targetIndex);
        }
    }

    private List<NodeLive> SortNodes(List<NodeLive> nodes)
    {
        return _sortMode switch
        {
            SortMode.LastHeard => nodes
                .OrderByDescending(n => n.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(n => n.LastHeardUtc)
                .ThenBy(n => n.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SortMode.HopsAway => nodes
                .OrderBy(n => n.HopsAway ?? int.MaxValue)
                .ThenByDescending(n => n.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(n => n.LastHeardUtc)
                .ThenBy(n => n.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SortMode.FavoritesFirst => nodes
                .OrderByDescending(n => n.IsFavorite)
                .ThenByDescending(n => n.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(n => n.LastHeardUtc)
                .ThenBy(n => n.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => nodes
                .OrderBy(n => n.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = HideInactiveToggle.IsChecked == true;
        RebuildVisibleNodes();
        TriggerMapUpdate();
    }

    private void ConnectedNodeChanged()
    {
        if (!EnsureOnUi(ConnectedNodeChanged))
            return;

        var scope = AppDataPaths.ActiveNodeScope;
        if (!string.Equals(_activeLogScope, scope, StringComparison.OrdinalIgnoreCase))
        {
            _activeLogScope = scope;
            _lastLogWriteByNode.Clear();
            _lastViewedByNode.Clear();
            _lastAppendedByNode.Clear();
            _pendingLogIndicatorsByNode.Clear();
            SeedLogWriteTimes();
            RefreshSelectedNodeLogs();
        }

        RebuildVisibleNodes();
        RefreshNodeDistanceAndDirection();
        ApplyNodeSorting();
        OnChanged(nameof(IsConnectedNodeSelected));
        OnChanged(nameof(IgnoreDeleteActionsVisibility));
        OnChanged(nameof(ConnectedNodeActionsVisibility));
        OnChanged(nameof(CanEditSelectedNodeState));
        OnChanged(nameof(CanRunConnectedNodeActions));
    }

    private void EnsureConnectedNodeInNodes()
    {
        if (string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
            return;

        var connected = AppState.Nodes.FirstOrDefault(n =>
            string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
        if (connected is not null)
            return;

        var node = new NodeLive(AppState.ConnectedNodeIdHex);
        AppState.Nodes.Insert(0, node);
    }

    private void NodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodesList.SelectedItem is NodeLive node)
            Selected = node;
    }

    private void NodesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        ZoomToNode_Click(sender, e);
    }

    private async System.Threading.Tasks.Task PushAllNodesToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;

        var nodes = MeshVenes.AppState.Nodes
            .Where(n => !IsHiddenByInactive(n))
            .Select(n => new
            {
                idHex = n.IdHex,
                name = n.Name,
                shortName = n.ShortName,
                shortId = n.ShortId,
                snr = n.SNR,
                rssi = n.RSSI,
                lastHeard = n.LastHeard,
                lat = n.Latitude,
                lon = n.Longitude,
                hasPos = n.HasPosition && IsValidMapPosition(n.Latitude, n.Longitude),
                lastPosLocal = n.LastPositionText
            })
            .ToArray();

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nodes", nodes }, s_jsonOptions));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task PushWaypointsToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        AppState.PurgeExpiredWaypoints();

        var waypoints = AppState.Waypoints
            .Where(w => !w.IsExpired)
            .Where(w => w.HasPosition)
            .Select(w => new
            {
                id = w.WaypointId,
                name = w.DisplayName,
                description = w.Description,
                lat = w.Latitude,
                lon = w.Longitude,
                iconGlyph = w.IconGlyph,
                iconCodepoint = w.IconCodepoint,
                lockedTo = w.LockedToNodeNum,
                expireUnix = w.ExpireUnixUtc,
                sourceNodeNum = w.SourceNodeNum,
                sourceIdHex = w.SourceIdHex,
                updatedUtc = w.LastUpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
                channel = w.ChannelIndex
            })
            .ToArray();

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "waypoints", waypoints }, s_jsonOptions));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task PushSelectionToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "selected", idHex = Selected?.IdHex }, s_jsonOptions));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void FitAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fitAll\"}");
    }

    private void TryAutoFitMapOnLoad()
    {
        if (!_pendingAutoFitOnLoad)
            return;

        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        _pendingAutoFitOnLoad = false;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fitAll\"}");
    }

    private void ZoomToNode_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        if (Selected is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "zoomTo", idHex = Selected.IdHex }, s_jsonOptions));
    }

    private void ToggleGpsTrack_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;

        var nodeId = NormalizeNodeId(Selected.IdHex);
        if (_enabledTrackNodeIds.Remove(nodeId))
        {
            _lastTrackPointByNode.Remove(nodeId);
            SendTrackClear(nodeId);
            OnChanged(nameof(GpsTrackButtonText));
            return;
        }

        _enabledTrackNodeIds.Add(nodeId);
        var points = GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .Where(p => IsValidMapPosition(p.Lat, p.Lon))
            .OrderBy(p => p.TsUtc)
            .Select(p => new MapGeoPoint(p.Lat, p.Lon))
            .ToArray();

        SendTrackSet(nodeId, points);
        if (points.Length > 0)
            _lastTrackPointByNode[nodeId] = (points[^1].Lat, points[^1].Lon);
        OnChanged(nameof(GpsTrackButtonText));
    }

    private void ResetMap_Click(object sender, RoutedEventArgs e)
    {
        _enabledTrackNodeIds.Clear();
        _lastTrackPointByNode.Clear();
        SendTrackClearAll();
        SendRouteClear();
        OnChanged(nameof(GpsTrackButtonText));
    }

    private void SendDm_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        MeshVenes.AppState.SetActiveChatPeer(Selected.IdHex);
        App.MainWindowInstance?.NavigateTo("messages");
    }

    private async void ExchangeUserInfo_Click(object sender, RoutedEventArgs _)
        => await SendRequestAsync("Exchange User Info", async nodeNum =>
            await RadioClient.Instance.SendNodeInfoRequestAsync(nodeNum));

    private async void ExchangePositions_Click(object sender, RoutedEventArgs _)
        => await SendRequestAsync("Exchange Positions", async nodeNum =>
            await RadioClient.Instance.SendPositionRequestAsync(nodeNum));

    private async void TraceRoute_Click(object sender, RoutedEventArgs _)
        => await SendTraceRouteAsync();

    private async void IgnoreNode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (IsConnectedNodeSelected)
        {
            await ShowStatusAsync("Ignore is not available for the currently connected node.");
            return;
        }

        if (!TryGetConnectedAdminNodeNum(out var adminNodeNum, out var adminError))
        {
            await ShowStatusAsync(adminError);
            return;
        }

        if (!TryGetSelectedNodeNum(out var targetNodeNum, out var targetError))
        {
            await ShowStatusAsync(targetError);
            return;
        }

        var isIgnoring = !(Selected?.IsIgnored ?? false);
        var actionText = isIgnoring ? "ignore" : "remove from ignored";
        var ok = await ConfirmActionAsync(
            "Confirm ignore",
            $"Are you sure you want to {actionText} node {Selected!.Name} ({Selected.ShortId})?",
            "Yes");

        if (!ok)
            return;

        try
        {
            await AdminConfigClient.Instance.SetIgnoredNodeAsync(adminNodeNum, targetNodeNum, isIgnoring);
            Selected.IsIgnored = isIgnoring;
            OnChanged(nameof(IgnoreNodeButtonText));
            RadioClient.Instance.AddLogFromUiThread(
                isIgnoring
                    ? $"Ignored node {Selected.Name} (0x{targetNodeNum:x8})."
                    : $"Unignored node {Selected.Name} (0x{targetNodeNum:x8}).");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Ignore update failed: {ex.Message}");
        }
    }

    private async void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (IsConnectedNodeSelected)
        {
            await ShowStatusAsync("Delete is not available for the currently connected node.");
            return;
        }

        if (!TryGetConnectedAdminNodeNum(out var adminNodeNum, out var adminError))
        {
            await ShowStatusAsync(adminError);
            return;
        }

        if (!TryGetSelectedNodeNum(out var targetNodeNum, out var targetError))
        {
            await ShowStatusAsync(targetError);
            return;
        }

        var ok = await ConfirmActionAsync(
            "Confirm delete",
            $"Are you sure you want to delete node {Selected!.Name} ({Selected.ShortId}) from NodeDB?",
            "Delete");

        if (!ok)
            return;

        var selectedNode = Selected;

        try
        {
            await AdminConfigClient.Instance.RemoveNodeAsync(adminNodeNum, targetNodeNum);
            if (selectedNode is not null)
                AppState.Nodes.Remove(selectedNode);

            RadioClient.Instance.AddLogFromUiThread($"Deleted node {selectedNode?.Name ?? "unknown"} (0x{targetNodeNum:x8}) from NodeDB.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Delete node failed: {ex.Message}");
        }
    }

    private async void RefreshDeviceMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!IsConnectedNodeSelected)
            return;

        if (!TryGetSelectedNodeNum(out var nodeNum, out var error))
        {
            await ShowStatusAsync(error);
            return;
        }

        try
        {
            var metadata = await AdminConfigClient.Instance.GetDeviceMetadataAsync(nodeNum);
            ApplyDeviceMetadata(metadata);
            RadioClient.Instance.AddLogFromUiThread($"Refreshed device metadata for {Selected?.Name ?? "connected node"}.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Refresh metadata failed: {ex.Message}");
        }
    }

    private async void PowerOffNode_Click(object sender, RoutedEventArgs e)
    {
        if (!IsConnectedNodeSelected)
            return;

        if (!TryGetSelectedNodeNum(out var nodeNum, out var error))
        {
            await ShowStatusAsync(error);
            return;
        }

        var ok = await ConfirmActionAsync(
            "Confirm power off",
            "Are you sure you want to power off the connected node?",
            "Power Off");

        if (!ok)
            return;

        try
        {
            await AdminConfigClient.Instance.ShutdownNodeAsync(nodeNum, 0);
            RadioClient.Instance.AddLogFromUiThread("Power off command sent to connected node.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Power off failed: {ex.Message}");
        }
    }

    private async void RebootNode_Click(object sender, RoutedEventArgs e)
    {
        if (!IsConnectedNodeSelected)
            return;

        if (!TryGetSelectedNodeNum(out var nodeNum, out var error))
        {
            await ShowStatusAsync(error);
            return;
        }

        var ok = await ConfirmActionAsync(
            "Confirm reboot",
            "Are you sure you want to reboot the connected node?",
            "Reboot");

        if (!ok)
            return;

        try
        {
            await AdminConfigClient.Instance.RebootNodeAsync(nodeNum, 0);
            RadioClient.Instance.AddLogFromUiThread("Reboot command sent to connected node.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Reboot failed: {ex.Message}");
        }
    }

    private async void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailsTabs.SelectedIndex < 0) return;
        UpdateTabHeaderColors();

        if (DetailsTabs.SelectedIndex == 0)
        {
            await HandleMapTabSelectedAsync();
        }

        var logKind = TabIndexToLogKind(DetailsTabs.SelectedIndex);
        if (logKind is null) return;

        if (Selected is not null)
            MarkTabViewed(logKind.Value, Selected.IdHex);

        RefreshSelectedNodeLogs();

    }

    private async System.Threading.Tasks.Task HandleMapTabSelectedAsync()
    {
        await EnsureMapAsync();
        _ = MapView.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
    }

    private void PositionLogList_SelectionChanged(object _, SelectionChangedEventArgs _1)
    {
        _selectedPositionEntry = PositionLogList.SelectedItem as PositionLogEntry;
        OnChanged(nameof(HasPositionSelection));
    }

    private void DeviceMetricsLogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceMetricsLogList.SelectedItem is DeviceMetricSample selectedSample)
        {
            DeviceMetricsGraph.HighlightSample(selectedSample.Timestamp);
        }
        else
        {
            DeviceMetricsGraph.ClearHighlight();
        }
    }

    private void EnvironmentMetricsLogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentMetricsLogList.SelectedItem is EnvironmentMetricSample selectedSample)
        {
            EnvironmentMetricsGraph.HighlightSample(selectedSample.TimestampUtc);
        }
        else
        {
            EnvironmentMetricsGraph.ClearHighlight();
        }
    }

    private void DeviceMetricsLogList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DeviceMetricSample sample)
            return;

        DeviceMetricsLogList.SelectedItem = sample;
        DeviceMetricsGraph.HighlightSample(sample.Timestamp);
    }

    private void EnvironmentMetricsLogList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EnvironmentMetricSample sample)
            return;

        EnvironmentMetricsLogList.SelectedItem = sample;
        EnvironmentMetricsGraph.HighlightSample(sample.TimestampUtc);
    }

    private void PositionLogList_ItemClick(object _, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PositionLogEntry entry)
            return;

        PositionLogList.SelectedItem = entry;
        DetailsTabs.SelectedIndex = 0;
        ShowPositionOnMap(entry.Lat, entry.Lon);
    }

    private void PositionLogEntry_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowPositionLogContextFlyout(sender);
    }

    private void ShowPositionLogContextFlyout(object sender)
    {
        if (sender is not FrameworkElement element)
            return;

        var entry = element.DataContext as PositionLogEntry;
        if (entry is not null)
            PositionLogList.SelectedItem = entry;

        var flyout = element.ContextFlyout;
        flyout?.ShowAt(element);
    }

    private void DeviceMetricSamples_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!EnsureOnUi(() => DeviceMetricSamples_CollectionChanged(sender, e)))
            return;

        if (_suspendMetricGraphRefresh)
            return;

        OnChanged(nameof(DeviceMetricsCountText));
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private void EnvironmentMetricSamples_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!EnsureOnUi(() => EnvironmentMetricSamples_CollectionChanged(sender, e)))
            return;

        if (_suspendMetricGraphRefresh)
            return;

        OnChanged(nameof(EnvironmentMetricsCountText));
        EnvironmentMetricsGraph.SetSamples(_environmentMetricSamples);
    }

    private void DeviceMetricsLogService_SampleAdded(string nodeId, DeviceMetricSample sample)
    {
        if (!EnsureOnUi(() => DeviceMetricsLogService_SampleAdded(nodeId, sample)))
            return;

        if (Selected is null || !string.Equals(NormalizeNodeId(Selected.IdHex), nodeId, StringComparison.OrdinalIgnoreCase))
            return;

        var viewer = FindScrollViewer(DeviceMetricsLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        if (_deviceMetricSamples.Count > 0 && _deviceMetricSamples[0].Timestamp == sample.Timestamp)
            return;

        _deviceMetricSamples.Insert(0, sample);
        if (_deviceMetricSamples.Count > 2000)
            _deviceMetricSamples.RemoveAt(_deviceMetricSamples.Count - 1);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private void RefreshDeviceMetricsSamples()
    {
        if (!EnsureOnUi(RefreshDeviceMetricsSamples))
            return;

        if (Selected is null)
            return;

        var viewer = FindScrollViewer(DeviceMetricsLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        var samples = DeviceMetricsLogService.GetSamples(Selected.IdHex, maxSamples: 2000);
        _suspendMetricGraphRefresh = true;
        try
        {
            _deviceMetricSamples.Clear();
            foreach (var sample in samples)
                _deviceMetricSamples.Add(sample);
        }
        finally
        {
            _suspendMetricGraphRefresh = false;
        }

        OnChanged(nameof(DeviceMetricsCountText));
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private void RefreshEnvironmentMetricsSamples()
    {
        if (!EnsureOnUi(RefreshEnvironmentMetricsSamples))
            return;

        if (Selected is null)
            return;

        var viewer = FindScrollViewer(EnvironmentMetricsLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        var samples = ReadEnvironmentMetricSamples();
        _suspendMetricGraphRefresh = true;
        try
        {
            _environmentMetricSamples.Clear();
            foreach (var sample in samples)
                _environmentMetricSamples.Add(sample);
        }
        finally
        {
            _suspendMetricGraphRefresh = false;
        }

        OnChanged(nameof(EnvironmentMetricsCountText));
        EnvironmentMetricsGraph.SetSamples(_environmentMetricSamples);
        EnvironmentMetricsLogText = BuildEnvironmentMetricsDisplayText(_environmentMetricSamples);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private void RefreshPositionEntries(IReadOnlyList<PositionLogEntry>? entries = null)
    {
        if (!EnsureOnUi(() => RefreshPositionEntries(entries)))
            return;

        var viewer = FindScrollViewer(PositionLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        var positionEntries = entries ?? ReadPositionEntries();
        PositionLogEntries.Clear();
        foreach (var entry in positionEntries)
            PositionLogEntries.Add(entry);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private async void PositionLogOpenMaps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is PositionLogEntry entry)
            await OpenMapsForPositionAsync(entry.Lat, entry.Lon);
    }

    private async void OpenCurrentPosition_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null || !Selected.HasPosition)
            return;

        await OpenMapsForPositionAsync(Selected.Latitude, Selected.Longitude);
    }

    private async System.Threading.Tasks.Task OpenMapsForPositionAsync(double latValue, double lonValue)
    {
        var lat = latValue.ToString("0.0000000", CultureInfo.InvariantCulture);
        var lon = lonValue.ToString("0.0000000", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://www.google.com/maps?q={lat},{lon}");
        await Launcher.LaunchUriAsync(uri);
    }

    private async void DeletePositionLogOlder_Click(object _, RoutedEventArgs _1)
        => await DeletePositionLogOlderAsync();

    private async void ExportPositionLog_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null)
            return;

        var entries = ReadPositionEntries();
        if (entries.Count == 0)
        {
            await ShowStatusAsync("No position log entries to export.");
            return;
        }

        var suggestedName = $"{Selected.ShortId}_position_log";
        var exportPath = await PickExportPathAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(exportPath))
            return;

        var extension = Path.GetExtension(exportPath).ToLowerInvariant();
        var content = extension switch
        {
            ".gpx" => BuildPositionLogGpx(entries, Selected),
            ".csv" => BuildPositionLogCsv(entries, Selected),
            ".txt" => BuildPositionLogTxt(entries),
            _ => BuildPositionLogCsv(entries, Selected)
        };

        try
        {
            var targetDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
                Directory.CreateDirectory(targetDir);
            File.WriteAllText(exportPath, content, Encoding.UTF8);
            await ShowStatusAsync($"Position log exported to:{Environment.NewLine}{exportPath}");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Export failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task<string?> PickExportPathAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        picker.FileTypeChoices.Add("GPX", new List<string> { ".gpx" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });

        if (App.MainWindowInstance is null)
            return null;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string BuildPositionLogTxt(IEnumerable<PositionLogEntry> entries)
        => string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayText));

    private static string BuildPositionLogCsv(IEnumerable<PositionLogEntry> entries, NodeLive node)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,lat,lon,alt,nodeId,nodeName");

        foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
        {
            var timestamp = entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture);
            var lat = entry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = entry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var alt = entry.Alt.HasValue ? entry.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
            var nodeId = EscapeCsv(node.IdHex ?? "");
            var nodeName = EscapeCsv(node.Name ?? "");

            sb.AppendLine($"{timestamp},{lat},{lon},{alt},{nodeId},{nodeName}");
        }

        return sb.ToString();
    }

    private static string BuildPositionLogGpx(IEnumerable<PositionLogEntry> entries, NodeLive node)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
            writer.WriteAttributeString("version", "1.1");
            writer.WriteAttributeString("creator", "MeshVenes");

            writer.WriteStartElement("trk");
            writer.WriteElementString("name", node.Name ?? "MeshVenes");
            writer.WriteStartElement("trkseg");

            foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
            {
                writer.WriteStartElement("trkpt");
                writer.WriteAttributeString("lat", entry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("lon", entry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture));
                if (entry.Alt.HasValue)
                    writer.WriteElementString("ele", entry.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteElementString("time", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private void ClearDeviceMetrics_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null) return;
        DeviceMetricsLogService.ClearSamples(Selected.IdHex);
        _deviceMetricSamples.Clear();
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private async void ShowLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        var folderPath = GetLogFolderPath(kind);
        try
        {
            Directory.CreateDirectory(folderPath);
            var opened = await Launcher.LaunchFolderPathAsync(folderPath);
            if (!opened)
                await ShowStatusAsync("Unable to open the log folder.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Unable to open the log folder: {ex.Message}");
        }
    }

    private async void DeleteLogOlder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        await DeleteLogOlderAsync(kind);
    }

    private async void DeleteLogAll_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        await DeleteLogAllAsync(kind);
    }

    private async void SaveDeviceMetrics_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null)
            return;

        var logPath = DeviceMetricsLogService.GetLogPath(Selected.IdHex);
        if (!File.Exists(logPath))
        {
            await ShowStatusAsync("No device metrics log found for this node yet.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"{Selected.ShortId}_device_metrics"
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });

        if (App.MainWindowInstance is null)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var content = await FileIO.ReadTextAsync(await StorageFile.GetFileFromPathAsync(logPath));
        await FileIO.WriteTextAsync(file, content);
    }

    private void ApplyPendingIndicatorsForSelectedNode()
    {
        if (Selected is null)
            return;

        ClearAllTabIndicators();

        if (_pendingLogIndicatorsByNode.TryGetValue(NormalizeNodeId(Selected.IdHex), out var pending))
        {
            foreach (var kind in pending)
                SetTabIndicator(kind, true);
        }
    }

    private void RefreshSelectedNodeLogs()
    {
        if (Selected is null)
            return;

        RefreshDeviceMetricsSamples();
        RefreshTraceRouteEntries();
        RefreshEnvironmentMetricsSamples();
        PowerMetricsLogText = ReadLogText(LogKind.PowerMetrics);
        DetectionSensorLogText = ReadLogText(LogKind.DetectionSensor);

        RefreshPositionEntries();

        _selectedPositionEntry = null;
        OnChanged(nameof(HasPositionSelection));
    }

    private void PollLogs()
    {
        foreach (var node in MeshVenes.AppState.Nodes)
        {
            if (node.IdHex is null) continue;

            foreach (var kind in AllLogKinds)
            {
                var newWriteTime = GetLogLastWriteTimeUtc(node.IdHex, kind);
                var lastWriteTime = GetLastLogWriteTime(node.IdHex, kind);

                if (newWriteTime == DateTime.MinValue)
                    continue;

                if (newWriteTime > lastWriteTime)
                {
                    SetLastLogWriteTime(node.IdHex, kind, newWriteTime);
                    HandleNewLog(node, kind, newWriteTime);
                }
            }
        }
    }

    private void HandleNewLog(NodeLive node, LogKind kind, DateTime appendedUtc)
    {
        var nodeId = NormalizeNodeId(node.IdHex);
        SetLastAppendedTimestamp(nodeId, kind, appendedUtc);

        var isSelectedNode = Selected is not null
            && string.Equals(NormalizeNodeId(Selected.IdHex), nodeId, StringComparison.OrdinalIgnoreCase);
        var isSelectedTab = IsTabSelectedForLog(kind);

        if (isSelectedNode && isSelectedTab)
        {
            SetLastViewedTimestamp(nodeId, kind, appendedUtc);
            ClearTabIndicator(kind);
            ClearPendingIndicator(nodeId, kind);
            RefreshSelectedNodeLogs();
            return;
        }

        var shouldShowIndicator = appendedUtc > GetLastViewedTimestamp(nodeId, kind);
        if (shouldShowIndicator)
        {
            if (isSelectedNode)
                SetTabIndicator(kind, true);
            AddPendingIndicator(nodeId, kind);
        }

        if (isSelectedNode)
            RefreshSelectedNodeLogs();
    }

    private bool IsTabSelectedForLog(LogKind kind)
        => TabIndexToLogKind(DetailsTabs.SelectedIndex) == kind;

    private void AddPendingIndicator(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_pendingLogIndicatorsByNode.TryGetValue(normalized, out var set))
        {
            set = new HashSet<LogKind>();
            _pendingLogIndicatorsByNode[normalized] = set;
        }

        set.Add(kind);
        UpdateNodeLogIndicator(normalized);
    }

    private void ClearPendingIndicator(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_pendingLogIndicatorsByNode.TryGetValue(normalized, out var set))
            return;

        set.Remove(kind);
        if (set.Count == 0)
            _pendingLogIndicatorsByNode.Remove(normalized);

        UpdateNodeLogIndicator(normalized);
    }

    private void ClearAllTabIndicators()
    {
        SetTabIndicator(LogKind.DeviceMetrics, false);
        SetTabIndicator(LogKind.Position, false);
        SetTabIndicator(LogKind.TraceRoute, false);
        SetTabIndicator(LogKind.EnvironmentMetrics, false);
        SetTabIndicator(LogKind.PowerMetrics, false);
        SetTabIndicator(LogKind.DetectionSensor, false);
    }

    private void SetTabIndicator(LogKind kind, bool value)
    {
        switch (kind)
        {
            case LogKind.DeviceMetrics:
                if (_deviceMetricsTabIndicator == value) return;
                _deviceMetricsTabIndicator = value;
                OnChanged(nameof(DeviceMetricsTabIndicatorVisibility));
                break;
            case LogKind.Position:
                if (_positionTabIndicator == value) return;
                _positionTabIndicator = value;
                OnChanged(nameof(PositionTabIndicatorVisibility));
                break;
            case LogKind.TraceRoute:
                if (_traceRouteTabIndicator == value) return;
                _traceRouteTabIndicator = value;
                OnChanged(nameof(TraceRouteTabIndicatorVisibility));
                break;
            case LogKind.EnvironmentMetrics:
                if (_environmentMetricsTabIndicator == value) return;
                _environmentMetricsTabIndicator = value;
                OnChanged(nameof(EnvironmentMetricsTabIndicatorVisibility));
                break;
            case LogKind.PowerMetrics:
                if (_powerMetricsTabIndicator == value) return;
                _powerMetricsTabIndicator = value;
                OnChanged(nameof(PowerMetricsTabIndicatorVisibility));
                break;
            case LogKind.DetectionSensor:
                if (_detectionSensorTabIndicator == value) return;
                _detectionSensorTabIndicator = value;
                OnChanged(nameof(DetectionSensorTabIndicatorVisibility));
                break;
        }
    }

    private void ClearTabIndicator(LogKind kind)
        => SetTabIndicator(kind, false);

    private void TraceRouteLogList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TraceRouteLogEntry entry)
            return;

        SelectedTraceRouteEntry = entry;
        if (TraceRouteDetailsTip is not null)
        {
            TraceRouteDetailsTip.Target = TraceRouteLogList.ContainerFromItem(entry) as FrameworkElement ?? TraceRouteLogList;
            TraceRouteDetailsTip.IsOpen = true;
        }
    }

    private void TraceRouteEntryViewMap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TraceRouteLogEntry entry)
            return;

        SelectedTraceRouteEntry = entry;
        if (TraceRouteDetailsTip is not null)
            TraceRouteDetailsTip.IsOpen = false;

        ViewTraceRouteOnMap(entry);
    }

    private void RouteContentScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        UpdateRouteHorizontalBarMetrics(source);
        if (_isSyncingRouteScroll)
            return;

        SyncRouteHorizontalScroll(source, "RouteHorizontalBarScroll");
    }

    private void RouteHorizontalBarScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isSyncingRouteScroll || sender is not ScrollViewer source)
            return;

        SyncRouteHorizontalScroll(source, "RouteContentScroll");
    }

    private void RouteContentScroll_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer source)
            UpdateRouteHorizontalBarMetrics(source);
    }

    private void RouteContentScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer source)
            UpdateRouteHorizontalBarMetrics(source);
    }

    private void SyncRouteHorizontalScroll(ScrollViewer source, string peerName)
    {
        var peer = FindPeerScrollViewerForTemplate(source, peerName);
        if (peer is null)
            return;

        if (Math.Abs(peer.HorizontalOffset - source.HorizontalOffset) < 0.5)
            return;

        try
        {
            _isSyncingRouteScroll = true;
            peer.ChangeView(source.HorizontalOffset, null, null, disableAnimation: true);
        }
        finally
        {
            _isSyncingRouteScroll = false;
        }
    }

    private void UpdateRouteHorizontalBarMetrics(ScrollViewer routeContentScroll)
    {
        var proxyScroll = FindPeerScrollViewerForTemplate(routeContentScroll, "RouteHorizontalBarScroll");
        var proxyContent = FindPeerElementForTemplate<FrameworkElement>(routeContentScroll, "RouteHorizontalBarContent");
        if (proxyScroll is null || proxyContent is null)
            return;

        var proxyViewport = proxyScroll.ActualWidth;
        if (proxyViewport <= 0)
            return;

        var contentWidth = proxyViewport + Math.Max(0, routeContentScroll.ScrollableWidth);
        proxyContent.Width = Math.Max(proxyViewport, contentWidth);
        var hasHorizontalOverflow = routeContentScroll.ScrollableWidth > 0.5;
        proxyScroll.IsEnabled = hasHorizontalOverflow;

        if (!hasHorizontalOverflow && proxyScroll.HorizontalOffset > 0)
            proxyScroll.ChangeView(0, null, null, disableAnimation: true);
    }

    private static T? FindPeerElementForTemplate<T>(FrameworkElement source, string peerName) where T : class
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement fe &&
                fe.FindName(peerName) is T peer &&
                !ReferenceEquals(peer, source))
            {
                return peer;
            }
        }

        return null;
    }

    private static ScrollViewer? FindPeerScrollViewerForTemplate(FrameworkElement source, string peerName)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement fe &&
                fe.FindName(peerName) is ScrollViewer peer &&
                !ReferenceEquals(peer, source))
            {
                return peer;
            }
        }

        return null;
    }

    private void TraceRouteHorizontalScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var point = e.GetCurrentPoint(source);
        var props = point.Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0)
            return;

        if (props.IsHorizontalMouseWheel &&
            string.Equals(source.Name, "RouteHorizontalBarScroll", StringComparison.Ordinal))
        {
            var targetHorizontal = Math.Max(0, source.HorizontalOffset - delta);
            source.ChangeView(targetHorizontal, null, null, disableAnimation: true);
            SyncRouteHorizontalScroll(source, "RouteHorizontalBarScroll");
            SyncRouteHorizontalScroll(source, "RouteContentScroll");
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var parentScroller = FindAncestorScrollViewer(source);
        if (parentScroller is null || ReferenceEquals(parentScroller, source))
            parentScroller = FindScrollViewer(TraceRouteLogList);

        if (parentScroller is null || ReferenceEquals(parentScroller, source))
            return;

        var targetOffset = Math.Max(0, parentScroller.VerticalOffset - delta);
        parentScroller.ChangeView(null, targetOffset, null, disableAnimation: true);
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is ScrollViewer viewer)
                return viewer;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void TraceRouteLogList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListView list)
            return;

        var props = e.GetCurrentPoint(list).Properties;
        if (props.IsHorizontalMouseWheel)
            return;

        var delta = props.MouseWheelDelta;
        if (delta == 0)
            return;

        var listScroller = FindScrollViewer(list);
        if (listScroller is null)
            return;

        var targetOffset = Math.Max(0, listScroller.VerticalOffset - delta);
        listScroller.ChangeView(null, targetOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private async void TraceRouteHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TraceRouteLogEntry entry)
            return;

        SelectedTraceRouteEntry = entry;
        await ShowTraceRouteHeaderDialogAsync(entry);
    }

    private void TraceRouteNodeInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RouteDisplayNode node)
            return;

        NavigateToNodeInList(node.NodeNum);
    }

    private void NavigateToNodeInList(uint nodeNum)
    {
        if (nodeNum == 0)
            return;

        var target = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        if (target is null)
            return;

        var wasHiddenByInactive = _hideInactive && IsHiddenByInactive(target);
        if (wasHiddenByInactive)
        {
            _hideInactive = false;
            if (HideInactiveToggle is not null)
                HideInactiveToggle.IsChecked = false;
        }

        if (SearchBox is not null && !string.IsNullOrWhiteSpace(SearchBox.Text))
            SearchBox.Text = string.Empty;

        RebuildVisibleNodes();
        ApplyNodeSorting();

        Selected = target;
        NodesList.SelectedItem = target;
        NodesList.ScrollIntoView(target);
    }

    private void TraceRouteViewMap_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTraceRouteEntry is null || !SelectedTraceRouteEntry.CanViewRoute)
            return;

        ViewTraceRouteOnMap(SelectedTraceRouteEntry);
    }

    private async System.Threading.Tasks.Task ShowTraceRouteHeaderDialogAsync(TraceRouteLogEntry entry)
    {
        var rootWidth = XamlRoot?.Size.Width ?? 1400;
        var rootHeight = XamlRoot?.Size.Height ?? 900;
        var desiredWidth = Math.Clamp(rootWidth - 100, 980, 1320);
        var desiredHeight = Math.Clamp(rootHeight - 70, 860, 1180);

        var dialogMap = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 700
        };

        var detailsPanel = BuildTraceRouteDetailPanel(entry);
        var detailsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = detailsPanel,
            Padding = new Thickness(10),
            MaxHeight = Math.Max(180, Math.Min(260, desiredHeight * 0.30))
        };

        var grid = new Grid
        {
            Width = desiredWidth,
            Height = desiredHeight
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var detailBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 60, 60)),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 22, 22, 22)),
            Child = detailsScroll,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(detailBorder, 0);
        grid.Children.Add(detailBorder);

        var mapBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 60, 60)),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 18, 18)),
            Child = dialogMap
        };
        Grid.SetRow(mapBorder, 1);
        grid.Children.Add(mapBorder);

        var dialog = new ContentDialog
        {
            Title = "Trace route details",
            PrimaryButtonText = "Copy details",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Content = grid
        };
        dialog.Resources["ContentDialogMaxWidth"] = desiredWidth + 36;
        dialog.Resources["ContentDialogMinWidth"] = desiredWidth;
        dialog.PrimaryButtonClick += (_, _) => _ = ClipboardUtil.TrySetText(BuildTraceRouteDetailsCopyText(entry));

        _ = InitializeTraceRouteDialogMapAsync(dialogMap, entry);
        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task InitializeTraceRouteDialogMapAsync(WebView2 dialogMap, TraceRouteLogEntry entry)
    {
        try
        {
            await dialogMap.EnsureCoreWebView2Async();
            var core = dialogMap.CoreWebView2;
            if (core is null)
                return;

            var mapFolder = _mapFolderPath;
            if (string.IsNullOrWhiteSpace(mapFolder) || !Directory.Exists(mapFolder))
            {
                var installPath = ResolveInstallPath();
                mapFolder = Path.GetFullPath(Path.Combine(installPath, "Assets"));
            }

            if (string.IsNullOrWhiteSpace(mapFolder) || !Directory.Exists(mapFolder))
                return;

            var routePayload = JsonSerializer.Serialize(new
            {
                type = "routeSet",
                forward = new { points = entry.ForwardPoints, qualities = entry.ForwardQualities },
                back = new { points = entry.BackPoints, qualities = entry.BackQualities }
            }, s_jsonOptions);

            var sent = false;
            void SendOnce()
            {
                if (sent || dialogMap.CoreWebView2 is null)
                    return;

                sent = true;
                dialogMap.CoreWebView2.PostWebMessageAsJson(routePayload);
            }

            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                    if (doc.RootElement.TryGetProperty("type", out var t) &&
                        string.Equals(t.GetString(), "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        SendOnce();
                    }
                }
                catch
                {
                    // ignore dialog map parse errors
                }
            };
            dialogMap.NavigationCompleted += async (_, navArgs) =>
            {
                if (navArgs.IsSuccess)
                {
                    if (dialogMap.CoreWebView2 is not null)
                        await dialogMap.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
                    SendOnce();
                }
            };

            core.SetVirtualHostNameToFolderMapping("routemap.local", mapFolder, CoreWebView2HostResourceAccessKind.Allow);
            dialogMap.Source = new Uri("https://routemap.local/Map/map.html");
        }
        catch
        {
            // dialog map is optional; detail panel remains useful even if map init fails
        }
    }

    private StackPanel BuildTraceRouteDetailPanel(TraceRouteLogEntry entry)
    {
        var panel = new StackPanel { Spacing = 10 };

        var allSnrValues = entry.ForwardDisplaySegments
            .Concat(entry.BackDisplaySegments)
            .Where(s => !string.IsNullOrWhiteSpace(s.SnrText) && s.SnrText != "--")
            .Select(s => TryParseSnrDb(s.SnrText))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var totalKm = entry.ForwardDisplaySegments
            .Concat(entry.BackDisplaySegments)
            .Select(s => TryParseDistanceKm(s.DistanceText))
            .Where(v => v.HasValue)
            .Sum(v => v!.Value);

        var headerRssi = TryExtractHeaderRssi(entry.HeaderText);
        var qualityText = allSnrValues.Count > 0
            ? $"min {allSnrValues.Min().ToString("0", CultureInfo.InvariantCulture)} dB / avg {allSnrValues.Average().ToString("0.0", CultureInfo.InvariantCulture)} dB"
            : "not available";

        panel.Children.Add(new TextBlock
        {
            Text = $"Timestamp: {entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Hops: {entry.HopCount} | Total km: {(totalKm > 0 ? totalKm.ToString("0.0", CultureInfo.InvariantCulture) : "--")} | RSSI: {(headerRssi.HasValue ? headerRssi.Value.ToString("0", CultureInfo.InvariantCulture) : "--")}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Route quality: {qualityText}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Forward route: {BuildFullRouteLine(entry.ForwardDisplayNodes)}",
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Forward links: {BuildSegmentOverviewLine(entry.ForwardDisplayNodes, entry.ForwardDisplaySegments)}",
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        if (entry.BackDisplayNodes.Count >= 2)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Back route: {BuildFullRouteLine(entry.BackDisplayNodes)}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Back links: {BuildSegmentOverviewLine(entry.BackDisplayNodes, entry.BackDisplaySegments)}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Nodes",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var uniqueNodes = entry.ForwardDisplayNodes
            .Concat(entry.BackDisplayNodes)
            .GroupBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var node in uniqueNodes)
        {
            var positionText = node.HasPosition && node.Latitude.HasValue && node.Longitude.HasValue
                ? $"{node.Latitude.Value.ToString("0.000000", CultureInfo.InvariantCulture)}, {node.Longitude.Value.ToString("0.000000", CultureInfo.InvariantCulture)}"
                : "No position";
            panel.Children.Add(new TextBlock
            {
                Text = $"LongName: {node.FullName}\nShortName: {node.ShortName}\nShortId: {node.ShortId}\nNodeId: {node.NodeId}\nPosition: {positionText}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Per-hop details",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        AppendHopDetailRows(panel, "FWD", entry.ForwardDisplayNodes, entry.ForwardDisplaySegments, headerRssi);
        AppendHopDetailRows(panel, "BACK", entry.BackDisplayNodes, entry.BackDisplaySegments, headerRssi);

        return panel;
    }

    private static string BuildFullRouteLine(IReadOnlyList<RouteDisplayNode> nodes)
    {
        if (nodes.Count == 0)
            return "(not received)";

        return string.Join(" -> ", nodes.Select(n => $"{n.FullName} {n.ShortId}"));
    }

    private static string BuildSegmentOverviewLine(
        IReadOnlyList<RouteDisplayNode> nodes,
        IReadOnlyList<RouteDisplaySegment> segments)
    {
        if (nodes.Count < 2 || segments.Count == 0)
            return "(not received)";

        var parts = new List<string>();
        var segmentCount = Math.Min(nodes.Count - 1, segments.Count);
        for (var i = 0; i < segmentCount; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrWhiteSpace(segment.DistanceText) && string.IsNullOrWhiteSpace(segment.SnrText))
                continue;

            var from = nodes[i].ShortId;
            var to = nodes[i + 1].ShortId;
            var distance = string.IsNullOrWhiteSpace(segment.DistanceText) ? "--km" : segment.DistanceText;
            var snr = string.IsNullOrWhiteSpace(segment.SnrText) ? "--" : segment.SnrText;
            parts.Add($"{from}->{to}: {distance} {snr}");
        }

        return parts.Count == 0 ? "(not received)" : string.Join(" | ", parts);
    }

    private static void AppendHopDetailRows(
        Panel host,
        string direction,
        IReadOnlyList<RouteDisplayNode> nodes,
        IReadOnlyList<RouteDisplaySegment> segments,
        double? rssi)
    {
        if (nodes.Count < 2 || segments.Count == 0)
            return;

        var segmentCount = Math.Min(nodes.Count - 1, segments.Count);
        for (var i = 0; i < segmentCount; i++)
        {
            var seg = segments[i];
            if (string.IsNullOrWhiteSpace(seg.DistanceText) && string.IsNullOrWhiteSpace(seg.SnrText))
                continue;

            var line = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.WrapWholeWords
            };
            line.Inlines.Add(new Run { Text = $"{direction} {nodes[i].FullName} -> {nodes[i + 1].FullName} | {seg.DistanceText} | " });
            line.Inlines.Add(new Run { Text = seg.SnrText, Foreground = seg.SnrBrush });
            line.Inlines.Add(new Run { Text = $" | RSSI {(rssi.HasValue ? rssi.Value.ToString("0", CultureInfo.InvariantCulture) : "--")} dB" });
            host.Children.Add(line);
        }
    }

    private static string BuildTraceRouteDetailsCopyText(TraceRouteLogEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine(entry.HeaderText);
        sb.AppendLine(entry.PathText);
        if (!string.IsNullOrWhiteSpace(entry.OverlayMetricsText))
            sb.AppendLine(entry.OverlayMetricsText);
        if (!string.IsNullOrWhiteSpace(entry.RouteBackHeaderText))
            sb.AppendLine(entry.RouteBackHeaderText);
        if (!string.IsNullOrWhiteSpace(entry.RouteBackPathText))
            sb.AppendLine(entry.RouteBackPathText);
        return sb.ToString().TrimEnd();
    }

    private void ViewTraceRouteOnMap(TraceRouteLogEntry entry)
    {
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        DetailsTabs.SelectedIndex = 0;
        SendRouteSet(
            entry.ForwardPoints,
            entry.ForwardQualities,
            entry.BackPoints,
            entry.BackQualities);
    }

    private void RefreshTraceRouteEntries()
    {
        if (Selected is null)
        {
            TraceRouteLogEntries.Clear();
            _traceRouteNodeId = null;
            return;
        }

        var lines = NodeLogArchive.ReadTail(ToArchiveType(LogKind.TraceRoute), Selected.IdHex, maxLines: 400);
        var entries = lines
            .Select(BuildTraceRouteEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();

        entries.Reverse();
        UpdateTraceRouteEntries(entries);
    }

    private void UpdateTraceRouteEntries(IReadOnlyList<TraceRouteLogEntry> entries)
    {
        var nodeId = Selected?.IdHex;
        if (nodeId is null)
        {
            TraceRouteLogEntries.Clear();
            _traceRouteNodeId = null;
            return;
        }

        if (!string.Equals(_traceRouteNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
        {
            TraceRouteLogEntries.Clear();
            foreach (var entry in entries)
                TraceRouteLogEntries.Add(entry);
            _traceRouteNodeId = nodeId;
            return;
        }

        if (TraceRouteLogEntries.Count == 0)
        {
            foreach (var entry in entries)
                TraceRouteLogEntries.Add(entry);
            return;
        }

        if (entries.Count == 0)
        {
            TraceRouteLogEntries.Clear();
            return;
        }

        var existingFirst = TraceRouteLogEntries[0].RawLine;
        var matchIndex = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].RawLine == existingFirst)
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            TraceRouteLogEntries.Clear();
            foreach (var entry in entries)
                TraceRouteLogEntries.Add(entry);
            return;
        }

        if (matchIndex > 0)
        {
            for (var i = matchIndex - 1; i >= 0; i--)
                TraceRouteLogEntries.Insert(0, entries[i]);
        }

        var minCount = Math.Min(TraceRouteLogEntries.Count, entries.Count);
        for (var i = 0; i < minCount; i++)
            TraceRouteLogEntries[i].UpdateFrom(entries[i]);

        if (entries.Count < TraceRouteLogEntries.Count)
        {
            for (var i = TraceRouteLogEntries.Count - 1; i >= entries.Count; i--)
                TraceRouteLogEntries.RemoveAt(i);
        }
        else if (entries.Count > TraceRouteLogEntries.Count)
        {
            for (var i = TraceRouteLogEntries.Count; i < entries.Count; i++)
                TraceRouteLogEntries.Add(entries[i]);
        }
    }

    private string ReadLogText(LogKind kind)
    {
        if (Selected is null)
            return "No log entries yet.";

        var lines = NodeLogArchive.ReadTail(ToArchiveType(kind), Selected.IdHex, maxLines: 400);
        if (lines.Length == 0)
            return "No log entries yet.";

        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<EnvironmentMetricSample> ReadEnvironmentMetricSamples()
    {
        if (Selected is null)
            return Array.Empty<EnvironmentMetricSample>();

        var lines = NodeLogArchive.ReadTail(ToArchiveType(LogKind.EnvironmentMetrics), Selected.IdHex, maxLines: 2000);
        var samples = new List<EnvironmentMetricSample>();

        foreach (var line in lines)
        {
            if (TryParseEnvironmentMetricSample(line, out var sample))
                samples.Add(sample);
        }

        return samples
            .OrderByDescending(sample => sample.TimestampUtc)
            .ToList();
    }

    private static string BuildEnvironmentMetricsDisplayText(IEnumerable<EnvironmentMetricSample> samples)
    {
        var lines = samples
            .Select(sample => $"{sample.TimestampText} | {sample.TemperatureDisplay} | {sample.HumidityDisplay} | {sample.PressureDisplay}")
            .ToArray();

        return lines.Length == 0 ? "No log entries yet." : string.Join(Environment.NewLine, lines);
    }

    private static bool TryParseEnvironmentMetricSample(string rawLine, out EnvironmentMetricSample sample)
    {
        sample = new EnvironmentMetricSample(DateTime.MinValue, null, null, null);
        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        var parts = rawLine.Split(new[] { " | " }, 2, StringSplitOptions.None);
        if (parts.Length < 2)
            return false;

        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tsUtc))
        {
            return false;
        }

        var payload = parts[1];
        var jsonStart = payload.IndexOf('{');
        var jsonEnd = payload.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
            return false;

        var json = payload.Substring(jsonStart, jsonEnd - jsonStart + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var temperature = ReadJsonDouble(root, "temperature");
            var humidity = ReadJsonDouble(root, "relativeHumidity")
                ?? ReadJsonDouble(root, "relative_humidity")
                ?? ReadJsonDouble(root, "humidity");
            var pressure = ReadJsonDouble(root, "barometricPressure")
                ?? ReadJsonDouble(root, "barometric_pressure")
                ?? ReadJsonDouble(root, "pressure");

            if (!temperature.HasValue && !humidity.HasValue && !pressure.HasValue)
                return false;

            sample = new EnvironmentMetricSample(tsUtc.ToUniversalTime(), temperature, humidity, pressure);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double? ReadJsonDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var asNumber))
            return asNumber;

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var asText))
        {
            return asText;
        }

        return null;
    }

    private IReadOnlyList<PositionLogEntry> ReadPositionEntries()
    {
        if (Selected is null)
            return Array.Empty<PositionLogEntry>();

        return GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.TsUtc)
            .Select(PositionLogEntry.FromPoint)
            .ToList();
    }

    private DateTime GetLastLogWriteTime(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastLogWriteByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastLogWriteTime(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastLogWriteByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastLogWriteByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private DateTime GetLastViewedTimestamp(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastViewedByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastViewedTimestamp(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastViewedByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastViewedByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private DateTime GetLastAppendedTimestamp(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastAppendedByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastAppendedTimestamp(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastAppendedByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastAppendedByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private static DateTime GetLogLastWriteTimeUtc(string nodeId, LogKind kind)
    {
        var path = GetLogFilePath(nodeId, kind);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string GetLogFilePath(string nodeId, LogKind kind)
    {
        var safe = SanitizeNodeId(nodeId);
        var baseDir = AppDataPaths.LogsPath;

        return kind switch
        {
            LogKind.DeviceMetrics => Path.Combine(baseDir, "device_metrics", $"0x{safe}.log"),
            LogKind.TraceRoute => Path.Combine(baseDir, "traceroute", $"0x{safe}.log"),
            LogKind.EnvironmentMetrics => Path.Combine(baseDir, "environment_metrics", $"0x{safe}.log"),
            LogKind.PowerMetrics => Path.Combine(baseDir, "power_metrics", $"0x{safe}.log"),
            LogKind.DetectionSensor => Path.Combine(baseDir, "detection_sensor", $"0x{safe}.log"),
            LogKind.Position => Path.Combine(baseDir, "gps", $"0x{safe}.log"),
            _ => Path.Combine(baseDir, "unknown", $"0x{safe}.log")
        };
    }

    private static string SanitizeNodeId(string idHex)
    {
        var safe = (idHex ?? "").Trim();
        if (safe.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            safe = safe[2..];

        safe = new string(safe.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = "UNKNOWN";

        return safe.ToUpperInvariant();
    }

    private static string NormalizeNodeId(string idHex)
        => $"0x{SanitizeNodeId(idHex)}";

    private void SeedLogWriteTimes()
    {
        foreach (var node in MeshVenes.AppState.Nodes)
            SeedLogWriteTimesForNode(node);
    }

    private void SeedLogWriteTimesForNode(NodeLive node)
    {
        if (node.IdHex is null)
            return;

        foreach (var kind in AllLogKinds)
        {
            var writeTime = GetLogLastWriteTimeUtc(node.IdHex, kind);
            if (writeTime != DateTime.MinValue)
                SetLastLogWriteTime(node.IdHex, kind, writeTime);
        }
    }

    private void MarkTabViewed(LogKind kind, string nodeId)
    {
        var appended = GetLastAppendedTimestamp(nodeId, kind);
        var now = DateTime.UtcNow;
        SetLastViewedTimestamp(nodeId, kind, appended > now ? appended : now);
        ClearTabIndicator(kind);
        ClearPendingIndicator(nodeId, kind);
    }

    private void UpdateNodeLogIndicator(string nodeId)
    {
        var normalized = NormalizeNodeId(nodeId);
        var hasPending = _pendingLogIndicatorsByNode.TryGetValue(normalized, out var set) && set.Count > 0;
        var node = MeshVenes.AppState.Nodes.FirstOrDefault(n =>
            string.Equals(NormalizeNodeId(n.IdHex), normalized, StringComparison.OrdinalIgnoreCase));

        if (node is null)
            return;

        var isSelected = Selected is not null
            && string.Equals(NormalizeNodeId(Selected.IdHex), normalized, StringComparison.OrdinalIgnoreCase);

        node.HasLogIndicator = hasPending && !isSelected;
    }

    private static LogKind? TabIndexToLogKind(int index)
        => index switch
        {
            1 => LogKind.DeviceMetrics,
            2 => LogKind.Position,
            3 => LogKind.TraceRoute,
            4 => LogKind.EnvironmentMetrics,
            5 => LogKind.PowerMetrics,
            6 => LogKind.DetectionSensor,
            _ => null
        };

    private async System.Threading.Tasks.Task SendRequestAsync(string actionName, Func<uint, System.Threading.Tasks.Task<uint>> action)
    {
        if (Selected is null) return;
        if (Selected.NodeNum == 0)
        {
            await ShowStatusAsync($"{actionName}: nodeNum is missing.");
            return;
        }

        try
        {
            var packetId = await action((uint)Selected.NodeNum);
            RadioClient.Instance.AddLogFromUiThread($"{actionName} sent to {Selected.Name} (packetId=0x{packetId:x8}).");
            await ShowStatusAsync($"{actionName} sent to {Selected.Name}.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"{actionName} failed: {ex.Message}");
        }
    }

    private bool TryGetConnectedAdminNodeNum(out uint nodeNum, out string error)
    {
        nodeNum = 0;
        if (!NodeIdentity.TryParseNodeNumFromHex(AppState.ConnectedNodeIdHex, out nodeNum) || nodeNum == 0)
        {
            error = "Admin action failed: connected node is missing.";
            return false;
        }

        error = "";
        return true;
    }

    private bool TryGetSelectedNodeNum(out uint nodeNum, out string error)
    {
        nodeNum = 0;
        if (Selected is null || Selected.NodeNum == 0 || Selected.NodeNum > uint.MaxValue)
        {
            error = "Action failed: selected node number is missing.";
            return false;
        }

        nodeNum = (uint)Selected.NodeNum;
        error = "";
        return true;
    }

    private void ApplyDeviceMetadata(Meshtastic.Protobufs.DeviceMetadata metadata)
    {
        if (Selected is null || metadata is null)
            return;

        if (!string.IsNullOrWhiteSpace(metadata.FirmwareVersion))
            Selected.FirmwareVersion = metadata.FirmwareVersion.Trim();

        var roleText = HumanizeToken(metadata.Role.ToString());
        if (IsMeaningfulMetadataValue(roleText))
            Selected.Role = roleText;

        var hwText = HumanizeToken(metadata.HwModel.ToString());
        if (IsMeaningfulMetadataValue(hwText))
            Selected.HardwareModel = hwText;
    }

    private static bool IsMeaningfulMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return !value.Equals("Unset", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        normalized = normalized.Replace('_', ' ').Replace('-', ' ');
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";

        for (var i = 0; i < words.Length; i++)
        {
            var lower = words[i].ToLowerInvariant();
            words[i] = lower switch
            {
                "wifi" => "WiFi",
                "ble" => "BLE",
                "lora" => "LoRa",
                "mqtt" => "MQTT",
                _ => char.ToUpperInvariant(lower[0]) + lower[1..]
            };
        }

        return string.Join(' ', words);
    }

    private async System.Threading.Tasks.Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async System.Threading.Tasks.Task ShowStatusAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "MeshVenes",
            PrimaryButtonText = "Close",
            XamlRoot = XamlRoot,
            Content = message
        };

        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task SendTraceRouteAsync()
    {
        if (Selected is null) return;
        if (_traceRouteCooldownActive) return;
        if (Selected.NodeNum == 0)
        {
            RadioClient.Instance.AddLogFromUiThread("Trace Route failed: nodeNum is missing.");
            return;
        }

        try
        {
            var packetId = await RadioClient.Instance.SendTraceRouteRequestAsync((uint)Selected.NodeNum);
            TraceRouteContext.RegisterActiveTraceRoute((uint)Selected.NodeNum);
            ScheduleTraceRouteNoResponse((uint)Selected.NodeNum, Selected.IdHex ?? "");
            RadioClient.Instance.AddLogFromUiThread($"Trace Route sent to {Selected.Name} (packetId=0x{packetId:x8}).");
            StartTraceRouteCooldown();
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Trace Route failed: {ex.Message}");
        }
    }

    private void StartTraceRouteCooldown()
    {
        _traceRouteCooldownActive = true;
        _traceRouteRemainingSeconds = 30;
        OnChanged(nameof(IsTraceRouteEnabled));
        OnChanged(nameof(TraceRouteButtonText));
        _traceRouteTimer.Start();
    }

    private void TraceRouteCooldownTick()
    {
        if (!_traceRouteCooldownActive) return;

        _traceRouteRemainingSeconds = Math.Max(0, _traceRouteRemainingSeconds - 1);
        OnChanged(nameof(TraceRouteButtonText));

        if (_traceRouteRemainingSeconds == 0)
        {
            _traceRouteCooldownActive = false;
            _traceRouteTimer.Stop();
            OnChanged(nameof(IsTraceRouteEnabled));
            OnChanged(nameof(TraceRouteButtonText));
        }
    }

    private void ScheduleTraceRouteNoResponse(uint targetNodeNum, string targetIdHex)
    {
        var timeoutSeconds = 30;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            _ = DispatcherQueue.TryEnqueue(() => LogTraceRouteNoResponse(targetNodeNum, targetIdHex));
        });
    }

    private void LogTraceRouteNoResponse(uint targetNodeNum, string targetIdHex)
    {
        if (!TraceRouteContext.TryMarkNoResponse(targetNodeNum))
            return;

        if (string.IsNullOrWhiteSpace(targetIdHex))
            return;

        uint? fromNodeNum = null;
        if (!string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
        {
            var connected = AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
            if (connected?.NodeNum > 0)
                fromNodeNum = (uint)connected.NodeNum;
        }

        var sb = new StringBuilder("active: true no_response: true ");
        if (fromNodeNum.HasValue)
            sb.Append("from: ").Append(fromNodeNum.Value).Append(' ');
        sb.Append("to: ").Append(targetNodeNum);

        NodeLogArchive.Append(NodeLogType.TraceRoute, targetIdHex, DateTime.UtcNow, sb.ToString());
        RefreshTraceRouteEntries();
    }

    private void ShowPositionOnMap(double lat, double lon)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "positionPeek", lat, lon };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private bool IsSelectedTrackEnabled
        => Selected is not null && _enabledTrackNodeIds.Contains(NormalizeNodeId(Selected.IdHex));

    private async System.Threading.Tasks.Task PushEnabledTracksToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;

        foreach (var nodeId in _enabledTrackNodeIds.ToList())
        {
            var points = GpsArchive.ReadAll(nodeId, maxPoints: 5000)
                .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
                .Where(p => IsValidMapPosition(p.Lat, p.Lon))
                .OrderBy(p => p.TsUtc)
                .Select(p => new MapGeoPoint(p.Lat, p.Lon))
                .ToArray();

            SendTrackSet(nodeId, points);
            if (points.Length > 0)
                _lastTrackPointByNode[nodeId] = (points[^1].Lat, points[^1].Lon);
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void TryAppendTrackPoint(NodeLive node)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        if (!node.HasPosition) return;
        if (!IsValidMapPosition(node.Latitude, node.Longitude)) return;
        var nodeId = NormalizeNodeId(node.IdHex);
        if (!_enabledTrackNodeIds.Contains(nodeId)) return;

        var point = (Lat: node.Latitude, Lon: node.Longitude);
        if (_lastTrackPointByNode.TryGetValue(nodeId, out var lastPoint))
        {
            if (Math.Abs(lastPoint.Lat - point.Lat) < 0.0000001 && Math.Abs(lastPoint.Lon - point.Lon) < 0.0000001)
                return;
        }

        _lastTrackPointByNode[nodeId] = point;
        SendTrackAppend(nodeId, point.Lat, point.Lon);
    }

    private void SendTrackSet(string nodeId, IReadOnlyList<MapGeoPoint> points)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackSet", idHex = nodeId, points };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackAppend(string nodeId, double lat, double lon)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackAppend", idHex = nodeId, point = new MapGeoPoint(lat, lon) };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackClear(string nodeId)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackClear", idHex = nodeId };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackClearAll()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"trackClearAll\"}");
    }

    private void SendRouteClear()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"routeClear\"}");
    }

    private void SendRouteSet(
        IReadOnlyList<RouteMapPoint> forwardPoints,
        IReadOnlyList<double?> forwardQualities,
        IReadOnlyList<RouteMapPoint> backPoints,
        IReadOnlyList<double?> backQualities)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new
        {
            type = "routeSet",
            forward = new { points = forwardPoints, qualities = forwardQualities },
            back = new { points = backPoints, qualities = backQualities }
        };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private static string FormatRelativeAge(DateTime utcTimestamp)
    {
        if (utcTimestamp == DateTime.MinValue)
            return "—";

        var delta = DateTime.UtcNow - utcTimestamp;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 60)
        {
            var minutes = Math.Max(1, (int)Math.Floor(delta.TotalMinutes));
            return $"{minutes} min ago";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)Math.Floor(delta.TotalHours);
            return $"{hours} hour{(hours == 1 ? "" : "s")} ago";
        }

        var days = (int)Math.Floor(delta.TotalDays);
        return $"{days} day{(days == 1 ? "" : "s")} ago";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";

        return duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private sealed record PositionLogKey(DateTime TimestampUtc, double Lat, double Lon);

    private sealed record GeoPointWithTimestamp(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("tsUtc")] string TsUtc);

    internal sealed record PositionLogEntry(DateTime TimestampUtc, double Lat, double Lon, double? Alt, string Src, string DisplayText)
    {
        public string TimestampText => TimestampUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture);
        public string CoordinatesDisplay => $"{Lat.ToString("0.0000000", CultureInfo.InvariantCulture)}, {Lon.ToString("0.0000000", CultureInfo.InvariantCulture)}";
        public string AltitudeDisplay => Alt.HasValue
            ? $"Alt {Alt.Value.ToString("0.##", CultureInfo.InvariantCulture)} m"
            : "Alt -";
        public string SourceDisplay => string.IsNullOrWhiteSpace(Src)
            ? "Src -"
            : $"Src {Src}";

        public bool HasValidPosition =>
            !double.IsNaN(Lat) &&
            !double.IsNaN(Lon) &&
            !double.IsInfinity(Lat) &&
            !double.IsInfinity(Lon) &&
            Lat is >= -90 and <= 90 &&
            Lon is >= -180 and <= 180;

        public static PositionLogEntry FromPoint(GpsArchive.PositionPoint point)
        {
            var altText = point.Alt.HasValue
                ? $" alt={point.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                : "";
            var lat = point.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = point.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var tsLocal = point.TsUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            var display = $"{tsLocal} | {lat}, {lon}{altText}";
            return new PositionLogEntry(point.TsUtc, point.Lat, point.Lon, point.Alt, point.Src, display);
        }
    }

    private static string FormatTraceRouteLine(string rawLine)
    {
        var entry = BuildTraceRouteEntry(rawLine);
        if (entry is null)
            return rawLine;

        var lines = new List<string>
        {
            entry.HeaderText,
            entry.PathText
        };

        if (entry.RouteBackVisibility == Visibility.Visible)
        {
            if (!string.IsNullOrWhiteSpace(entry.RouteBackHeaderText))
                lines.Add(entry.RouteBackHeaderText);
            if (!string.IsNullOrWhiteSpace(entry.RouteBackPathText))
                lines.Add(entry.RouteBackPathText);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static DateTimeOffset? TryParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    private static TraceRouteLogEntry? BuildTraceRouteEntry(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return null;

        var parts = rawLine.Split(new[] { " | " }, 2, StringSplitOptions.None);
        var tsPart = parts.Length > 0 ? parts[0] : "";
        var summary = parts.Length > 1 ? parts[1] : "";

        var timestamp = TryParseTimestamp(tsPart);
        var tsText = timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? tsPart;

        var parsed = ParseTraceRouteSummary(summary);
        var isActive = parsed.IsActive;
        var isPassive = !isActive;
        var tag = isActive ? "[ACTIVE]" : "[PASSIVE]";
        if (parsed.IsNoResponse)
        {
            var noResponseFrom = ResolveNodeIdentityDetailed(parsed.FromNode);
            var noResponseTo = ResolveNodeIdentityDetailed(parsed.ToNode);
            var noResponseHeader = $"{tag} {tsText} · 0 hops · -- km · RSSI --";
            var noResponseLine = $"Route: {noResponseFrom} -> {noResponseTo} (no response)";
            var noResponseRouteNodes = BuildDisplayRouteNodes(Array.Empty<uint>(), parsed.FromNode, parsed.ToNode);
            var noResponseDisplayNodes = BuildRouteDisplayNodes(noResponseRouteNodes);
            var noResponseDisplaySegments = BuildRouteDisplaySegments(noResponseRouteNodes, Array.Empty<int>(), out _);
            var noResponseContentWidth = BuildRouteContentWidth(noResponseDisplayNodes, Array.Empty<RouteDisplayNode>());
            return new TraceRouteLogEntry(
                rawLine,
                timestamp?.UtcDateTime ?? DateTime.MinValue,
                noResponseHeader,
                noResponseLine,
                null,
                null,
                noResponseHeader,
                noResponseLine,
                null,
                null,
                isPassive,
                0,
                Array.Empty<RouteMapPoint>(),
                Array.Empty<RouteMapPoint>(),
                Array.Empty<double?>(),
                Array.Empty<double?>(),
                false,
                noResponseContentWidth,
                noResponseDisplayNodes,
                noResponseDisplaySegments,
                Array.Empty<RouteDisplayNode>(),
                Array.Empty<RouteDisplaySegment>());
        }

        var hasForward = parsed.Route.Count > 0;
        var hasBack = parsed.RouteBack.Count > 0;
        var hasBackViaSnrOnly = !hasBack &&
                                isActive &&
                                parsed.SnrBack.Count > 0 &&
                                parsed.FromNode.HasValue &&
                                parsed.ToNode.HasValue;
        var backRoute = parsed.RouteBack;
        if (hasBackViaSnrOnly)
            backRoute = new List<uint> { parsed.ToNode!.Value, parsed.FromNode!.Value };

        var forwardRouteNodes = BuildDisplayRouteNodes(parsed.Route, parsed.FromNode, parsed.ToNode);
        var backRouteNodes = BuildDisplayRouteNodes(backRoute, parsed.ToNode, parsed.FromNode);
        var showBack = hasBack || hasBackViaSnrOnly;

        var fromNode = parsed.FromNode
            ?? (parsed.Route.Count > 0 ? (uint?)parsed.Route[0] : null)
            ?? (parsed.RouteBack.Count > 0 ? (uint?)parsed.RouteBack[0] : null);
        var toNode = parsed.ToNode
            ?? (parsed.Route.Count > 0 ? (uint?)parsed.Route[^1] : null)
            ?? (parsed.RouteBack.Count > 0 ? (uint?)parsed.RouteBack[^1] : null);

        var fromText = ResolveNodeIdentityDetailed(fromNode);
        var toText = ResolveNodeIdentityDetailed(toNode);
        var forwardHopCount = CountRelayHops(forwardRouteNodes, parsed.FromNode, parsed.ToNode);
        var backHopCount = CountRelayHops(backRouteNodes, parsed.ToNode, parsed.FromNode);
        var hopCount = hasForward ? forwardHopCount : (showBack ? backHopCount : forwardHopCount);

        var forwardDisplayNodes = BuildRouteDisplayNodes(forwardRouteNodes);
        var forwardDisplaySegments = BuildRouteDisplaySegments(forwardRouteNodes, parsed.SnrTowards, out var forwardTotalKm);
        var header = BuildTraceHeaderLine(tag, tsText, hopCount, forwardTotalKm, parsed.RxRssi);

        var routeLine = BuildRouteDisplayLine("Route", forwardRouteNodes, fromText, toText);
        if (forwardRouteNodes.Count < 2 && fromNode.HasValue && toNode.HasValue)
            routeLine = $"Route: {fromText} -> {toText}";
        if (forwardTotalKm.HasValue)
            routeLine = $"{routeLine} (Total km: {forwardTotalKm.Value.ToString("0.#", CultureInfo.InvariantCulture)})";
        var forwardDetailLine = BuildFullSegmentLine("Links", forwardRouteNodes, parsed.SnrTowards, includeRxRssi: true, parsed.RxRssi);

        string? routeBackLine = null;
        string? routeBackDetailLine = null;
        IReadOnlyList<RouteDisplayNode> backDisplayNodes = Array.Empty<RouteDisplayNode>();
        IReadOnlyList<RouteDisplaySegment> backDisplaySegments = Array.Empty<RouteDisplaySegment>();
        if (showBack)
        {
            var backFromText = ResolveNodeIdentityDetailed(parsed.ToNode ?? backRouteNodes[0]);
            var backToText = ResolveNodeIdentityDetailed(parsed.FromNode ?? backRouteNodes[^1]);
            routeBackLine = BuildRouteDisplayLine("Route Back", backRouteNodes, backFromText, backToText);
            backDisplayNodes = BuildRouteDisplayNodes(backRouteNodes);
            backDisplaySegments = BuildRouteDisplaySegments(backRouteNodes, parsed.SnrBack, out var backTotalKm);
            if (backTotalKm.HasValue)
                routeBackLine = $"{routeBackLine} (Total km: {backTotalKm.Value.ToString("0.#", CultureInfo.InvariantCulture)})";
            routeBackDetailLine = BuildFullSegmentLine("Back links", backRouteNodes, parsed.SnrBack, includeRxRssi: false, null);
        }
        var routeContentWidth = BuildRouteContentWidth(forwardDisplayNodes, backDisplayNodes);

        var overlayHeader = header;
        var overlayRoute = routeLine;
        var overlayBack = routeBackLine;
        var overlayMetrics = BuildPopupMetrics(parsed);

        var (forwardPoints, forwardQualities) = BuildRouteMapPoints(parsed.Route, parsed.FromNode, parsed.ToNode, parsed.SnrTowards);
        var (backPoints, backQualities) = BuildRouteMapPoints(backRoute, parsed.ToNode, parsed.FromNode, parsed.SnrBack);
        var canViewRoute = forwardPoints.Count >= 2 || backPoints.Count >= 2;

        return new TraceRouteLogEntry(
            rawLine,
            timestamp?.UtcDateTime ?? DateTime.MinValue,
            header,
            routeLine,
            routeBackLine,
            routeBackDetailLine,
            overlayHeader,
            overlayRoute,
            overlayBack,
            forwardDetailLine ?? overlayMetrics,
            isPassive,
            hopCount,
            forwardPoints,
            backPoints,
            forwardQualities,
            backQualities,
            canViewRoute,
            routeContentWidth,
            forwardDisplayNodes,
            forwardDisplaySegments,
            backDisplayNodes,
            backDisplaySegments);
    }

    private static List<uint> BuildDisplayRouteNodes(IReadOnlyList<uint> route, uint? fromNode, uint? toNode)
    {
        var nodes = new List<uint>(route);
        if (nodes.Count == 0)
        {
            if (fromNode.HasValue)
                nodes.Add(fromNode.Value);
            if (toNode.HasValue && (!fromNode.HasValue || toNode.Value != fromNode.Value))
                nodes.Add(toNode.Value);
            return nodes;
        }

        if (fromNode.HasValue && nodes[0] != fromNode.Value)
            nodes.Insert(0, fromNode.Value);
        if (toNode.HasValue && nodes[^1] != toNode.Value)
            nodes.Add(toNode.Value);
        return nodes;
    }

    private static int CountRelayHops(IReadOnlyList<uint> routeNodes, uint? fromNode, uint? toNode)
    {
        if (routeNodes.Count == 0)
            return 0;

        var includesFrom = fromNode.HasValue && routeNodes[0] == fromNode.Value;
        var includesTo = toNode.HasValue && routeNodes[^1] == toNode.Value;

        if (includesFrom && includesTo)
            return Math.Max(0, routeNodes.Count - 2);
        if (includesFrom || includesTo)
            return Math.Max(0, routeNodes.Count - 1);
        return routeNodes.Count;
    }

    private static string BuildRouteDisplayLine(string label, IReadOnlyList<uint> nodes, string fallbackFrom, string fallbackTo)
    {
        if (nodes.Count >= 2)
            return $"{label}: {string.Join(" -> ", nodes.Select(ResolveHopLabel))}";
        if (nodes.Count == 1)
            return $"{label}: {ResolveHopLabel(nodes[0])}";
        if (!string.IsNullOrWhiteSpace(fallbackFrom) || !string.IsNullOrWhiteSpace(fallbackTo))
        {
            var fromText = string.IsNullOrWhiteSpace(fallbackFrom) ? "Unknown node" : fallbackFrom;
            var toText = string.IsNullOrWhiteSpace(fallbackTo) ? "Unknown node" : fallbackTo;
            return $"{label}: {fromText} -> {toText}";
        }

        return $"{label}: (not received)";
    }

    private static string BuildTraceHeaderLine(string tag, string tsText, int hopCount, double? totalKm, double? rssi)
    {
        var kmText = totalKm.HasValue
            ? totalKm.Value.ToString("0.#", CultureInfo.InvariantCulture)
            : "--";
        var rssiText = rssi.HasValue
            ? rssi.Value.ToString("0", CultureInfo.InvariantCulture)
            : "--";
        return $"{tag} {tsText} · {hopCount} hops · {kmText} km · RSSI {rssiText}";
    }

    private static IReadOnlyList<RouteDisplayNode> BuildRouteDisplayNodes(IReadOnlyList<uint> routeNodes)
    {
        if (routeNodes.Count == 0)
            return Array.Empty<RouteDisplayNode>();

        var nodes = new List<RouteDisplayNode>(routeNodes.Count);
        foreach (var nodeNum in routeNodes)
        {
            var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
            var rawLongName = !string.IsNullOrWhiteSpace(node?.LongName)
                ? node!.LongName!
                : ResolveHopLabel(nodeNum);
            var shortName = !string.IsNullOrWhiteSpace(node?.ShortName) ? node!.ShortName! : "—";
            var nodeId = node?.IdHex ?? $"0x{nodeNum:x8}";
            var shortId = ResolveRouteShortId(node, nodeNum);
            var longName = TrimTrailingShortId(rawLongName, shortId);
            var displayName = $"{TruncateMiddle(longName, 18)} {shortId}";
            var hasPos = node is not null && node.HasPosition && IsValidMapPosition(node.Latitude, node.Longitude);
            var lat = hasPos ? node!.Latitude : (double?)null;
            var lon = hasPos ? node!.Longitude : (double?)null;
            var tooltip = $"Long name: {longName}\nShort name: {shortName}\nShort ID: {shortId}\nNode ID: {nodeId}";
            nodes.Add(new RouteDisplayNode(
                nodeNum,
                displayName,
                shortName,
                shortId,
                longName,
                nodeId,
                hasPos,
                lat,
                lon,
                tooltip));
        }

        return nodes;
    }

    private static IReadOnlyList<RouteDisplaySegment> BuildRouteDisplaySegments(
        IReadOnlyList<uint> routeNodes,
        IReadOnlyList<int> snrValues,
        out double? totalKm)
    {
        totalKm = null;
        if (routeNodes.Count < 2)
            return Array.Empty<RouteDisplaySegment>();

        var segments = new List<RouteDisplaySegment>(routeNodes.Count);
        var total = 0.0;
        var knownCount = 0;
        var segmentCount = routeNodes.Count - 1;

        for (var i = 0; i < segmentCount; i++)
        {
            var fromNum = routeNodes[i];
            var toNum = routeNodes[i + 1];
            var distanceText = "--km";
            if (TryGetNodePosition(fromNum, out var fromLat, out var fromLon) &&
                TryGetNodePosition(toNum, out var toLat, out var toLon))
            {
                var km = CalculateDistanceKm(fromLat, fromLon, toLat, toLon);
                distanceText = $"{km.ToString("0.#", CultureInfo.InvariantCulture)}km";
                total += km;
                knownCount++;
            }

            int? snr = null;
            if (i < snrValues.Count)
                snr = snrValues[i];
            else if (snrValues.Count == 1)
                snr = snrValues[0];

            var snrText = snr.HasValue ? $"{FormatSignedSnr(snr.Value)} dB" : "--";
            var brush = snr.HasValue ? GetSnrBrush(snr.Value) : SnrUnknownBrush;
            var tooltip = snr.HasValue
                ? $"Distance: {distanceText} | SNR: {snrText}"
                : $"Distance: {distanceText} | SNR: not received";
            segments.Add(new RouteDisplaySegment(distanceText, snrText, brush, tooltip));
        }

        // Keep one empty trailing cell so segment row aligns under node columns.
        segments.Add(new RouteDisplaySegment(string.Empty, string.Empty, SnrUnknownBrush, string.Empty));
        if (knownCount > 0)
            totalKm = total;
        return segments;
    }

    private static double BuildRouteContentWidth(
        IReadOnlyList<RouteDisplayNode> forwardNodes,
        IReadOnlyList<RouteDisplayNode> backNodes)
    {
        var maxNodeCount = Math.Max(Math.Max(forwardNodes.Count, backNodes.Count), 1);
        const double labelColumn = 56;
        const double nodeCellWidth = 240;
        return labelColumn + (maxNodeCount * nodeCellWidth);
    }

    private static string? BuildFullSegmentLine(
        string lineLabel,
        IReadOnlyList<uint> routeNodes,
        IReadOnlyList<int> snrValues,
        bool includeRxRssi,
        double? rxRssi)
    {
        if (routeNodes.Count < 2)
            return null;

        var parts = new List<string>();
        var segmentCount = routeNodes.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var fromNum = routeNodes[i];
            var toNum = routeNodes[i + 1];
            var fromLabel = ResolveHopLabel(fromNum);
            var toLabel = ResolveHopLabel(toNum);
            var distanceText = "--km";
            if (TryGetNodePosition(fromNum, out var fromLat, out var fromLon) &&
                TryGetNodePosition(toNum, out var toLat, out var toLon))
            {
                var km = CalculateDistanceKm(fromLat, fromLon, toLat, toLon);
                distanceText = $"{km.ToString("0.#", CultureInfo.InvariantCulture)}km";
            }

            int? snr = null;
            if (i < snrValues.Count)
                snr = snrValues[i];
            else if (snrValues.Count == 1)
                snr = snrValues[0];
            var snrText = snr.HasValue ? $"{FormatSignedSnr(snr.Value)} dB" : "--";

            parts.Add($"{fromLabel}->{toLabel} {distanceText} {snrText}");
        }

        var line = $"{lineLabel}: {string.Join(" | ", parts)}";
        if (includeRxRssi && rxRssi.HasValue)
            line += $" | RSSI {rxRssi.Value.ToString("0", CultureInfo.InvariantCulture)}";
        return line;
    }

    private static string? BuildPopupMetrics(TraceRouteParsed parsed)
    {
        var lines = new List<string>();

        var radioParts = new List<string>();
        if (parsed.RxSnr.HasValue)
            radioParts.Add($"RX SNR {FormatSnrValue(parsed.RxSnr.Value)}");
        if (parsed.RxRssi.HasValue)
            radioParts.Add($"RX RSSI {parsed.RxRssi.Value.ToString("0.0", CultureInfo.InvariantCulture)}");
        if (radioParts.Count > 0)
            lines.Add("Radio: " + string.Join(" | ", radioParts));

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    private static bool TryGetNodePosition(uint nodeNum, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;

        var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        if (node is null || !node.HasPosition)
            return false;
        if (!IsValidMapPosition(node.Latitude, node.Longitude))
            return false;

        lat = node.Latitude;
        lon = node.Longitude;
        return true;
    }

    private static (IReadOnlyList<RouteMapPoint> Points, IReadOnlyList<double?> Qualities) BuildRouteMapPoints(
        IReadOnlyList<uint> hops,
        uint? fromNode,
        uint? toNode,
        IReadOnlyList<int> snrValues)
    {
        var route = new List<uint>();
        if (hops.Count > 0)
        {
            route.AddRange(hops);
            if (fromNode.HasValue && route[0] != fromNode.Value)
                route.Insert(0, fromNode.Value);
            if (toNode.HasValue && route[^1] != toNode.Value)
                route.Add(toNode.Value);
        }
        else
        {
            if (fromNode.HasValue)
                route.Add(fromNode.Value);
            if (toNode.HasValue && (!fromNode.HasValue || toNode.Value != fromNode.Value))
                route.Add(toNode.Value);
        }

        if (route.Count == 0)
            return (Array.Empty<RouteMapPoint>(), Array.Empty<double?>());

        var points = new List<RouteMapPoint>();
        foreach (var nodeNum in route)
        {
            var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
            if (node is null || !node.HasPosition)
                continue;

            points.Add(new RouteMapPoint(node.Latitude, node.Longitude, ResolveHopLabel(nodeNum)));
        }

        var segmentCount = Math.Max(0, points.Count - 1);
        var qualities = new List<double?>(segmentCount);
        for (var i = 0; i < segmentCount; i++)
        {
            qualities.Add(i < snrValues.Count ? snrValues[i] : null);
        }

        return (points, qualities);
    }

    private static string ResolveHopLabel(uint nodeNum)
    {
        var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        if (node is null)
            return $"0x{nodeNum:x8}";

        if (!string.IsNullOrWhiteSpace(node.LongName))
            return node.LongName;
        if (!string.IsNullOrWhiteSpace(node.ShortName))
            return node.ShortName;
        if (!string.IsNullOrWhiteSpace(node.ShortId))
            return node.ShortId;
        return node.IdHex ?? $"0x{nodeNum:x8}";
    }

    private static string ResolveNodeIdentityDetailed(uint? nodeNum)
    {
        if (!nodeNum.HasValue)
            return "Unknown node";

        var node = MeshVenes.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum.Value);
        var hex = node?.IdHex ?? $"0x{nodeNum.Value:x8}";
        var shortName = node?.ShortName;
        var longName = node?.LongName;
        var shortId = node?.ShortId;

        if (!string.IsNullOrWhiteSpace(longName))
        {
            var detail = ResolveShortIdentity(shortName, shortId, hex, nodeNum.Value);
            return $"{longName} ({detail})";
        }

        return ResolveShortIdentity(shortName, shortId, hex, nodeNum.Value);
    }

    private static string ResolveShortIdentity(string? shortName, string? shortId, string hex, uint nodeNum)
    {
        var shortLabel = !string.IsNullOrWhiteSpace(shortName)
            ? shortName!
            : !string.IsNullOrWhiteSpace(shortId)
                ? shortId!
                : hex;

        if (!string.IsNullOrWhiteSpace(shortLabel) && !string.Equals(shortLabel, hex, StringComparison.OrdinalIgnoreCase))
            return $"{shortLabel} / {hex}";

        return !string.IsNullOrWhiteSpace(hex) ? hex : nodeNum.ToString(CultureInfo.InvariantCulture);
    }

    private static TraceRouteParsed ParseTraceRouteSummary(string summary)
    {
        var route = new List<uint>();
        var snrTowards = new List<int>();
        var routeBack = new List<uint>();
        var snrBack = new List<int>();
        double? rxSnr = null;
        double? rxRssi = null;
        uint? fromNode = null;
        uint? toNode = null;
        uint? channel = null;
        string? variant = null;
        bool isPassive = false;
        bool isActive = false;
        bool isNoResponse = false;

        var tokens = (summary ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLabel = "";

        foreach (var rawToken in tokens)
        {
            var token = NormalizeTraceToken(rawToken);
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (token.EndsWith(":", StringComparison.Ordinal))
            {
                currentLabel = NormalizeTraceLabel(token);
                continue;
            }

            switch (currentLabel)
            {
                case "route":
                    if (TryParseUIntToken(token, out var routeNum))
                        route.Add(routeNum);
                    break;
                case "snrtowards":
                    if (TryParseIntToken(token, out var snrTo))
                        snrTowards.Add(snrTo);
                    break;
                case "routeback":
                    if (TryParseUIntToken(token, out var routeBackNum))
                        routeBack.Add(routeBackNum);
                    break;
                case "snrback":
                    if (TryParseIntToken(token, out var snrBk))
                        snrBack.Add(snrBk);
                    break;
                case "rxsnr":
                    if (TryParseDoubleToken(token, out var rxSnrValue))
                        rxSnr = rxSnrValue;
                    break;
                case "rxrssi":
                    if (TryParseDoubleToken(token, out var rxRssiValue))
                        rxRssi = rxRssiValue;
                    break;
                case "from":
                    if (TryParseUIntToken(token, out var fromValue))
                        fromNode = fromValue;
                    break;
                case "to":
                    if (TryParseUIntToken(token, out var toValue))
                        toNode = toValue;
                    break;
                case "channel":
                    if (TryParseUIntToken(token, out var channelValue))
                        channel = channelValue;
                    break;
                case "variant":
                    variant = token;
                    break;
                case "passive":
                    if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token, "yes", StringComparison.OrdinalIgnoreCase) ||
                        token == "1")
                    {
                        isPassive = true;
                    }
                    break;
                case "active":
                    if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token, "yes", StringComparison.OrdinalIgnoreCase) ||
                        token == "1")
                    {
                        isActive = true;
                    }
                    break;
                case "noresponse":
                    if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token, "yes", StringComparison.OrdinalIgnoreCase) ||
                        token == "1")
                    {
                        isNoResponse = true;
                    }
                    break;
            }
        }

        return new TraceRouteParsed(route, snrTowards, routeBack, snrBack, rxSnr, rxRssi, fromNode, toNode, channel, variant, isPassive, isActive, isNoResponse);
    }

    private static string NormalizeTraceToken(string token)
    {
        return (token ?? "")
            .Trim()
            .TrimStart('{', '[')
            .TrimEnd('}', ']', ',')
            .Trim('"');
    }

    private static string NormalizeTraceLabel(string labelToken)
    {
        var label = labelToken;
        if (label.EndsWith(":", StringComparison.Ordinal))
            label = label[..^1];

        label = label.Trim().Trim('"').Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return label;
    }

    private static bool TryParseUIntToken(string token, out uint value)
    {
        var text = token.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseIntToken(string token, out int value)
    {
        var text = token.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            value = hexValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseDoubleToken(string token, out double value)
        => double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static double? TryParseSnrDb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = text.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static double? TryParseDistanceKm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = text.Replace("km", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static double? TryExtractHeaderRssi(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string marker = "RSSI ";
        var idx = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var valuePart = header[(idx + marker.Length)..];
        var token = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static string FormatSnrValue(int value)
        => FormatSnrValue((double)value);

    private static string FormatSnrValue(double value)
        => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatSignedSnr(int value)
        => value >= 0
            ? "+" + value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0", CultureInfo.InvariantCulture);

    private static Brush GetSnrBrush(double snrDb)
    {
        if (snrDb > 30)
            return SnrExcellentBrush;
        if (snrDb >= 10)
            return SnrGoodBrush;
        if (snrDb >= 0)
            return SnrFairBrush;
        return SnrPoorBrush;
    }

    private static string ResolveRouteShortId(NodeLive? node, uint nodeNum)
    {
        if (!string.IsNullOrWhiteSpace(node?.ShortId))
            return node.ShortId!.TrimStart('!').ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(node?.IdHex) && node.IdHex!.Length >= 4)
            return node.IdHex[^4..].ToLowerInvariant();

        return nodeNum.ToString("x8", CultureInfo.InvariantCulture)[^4..];
    }

    private static string TruncateMiddle(string? value, int maxChars)
    {
        var text = value ?? "";
        if (maxChars < 5 || text.Length <= maxChars)
            return text;

        const string ellipsis = "…";
        var charsToKeep = maxChars - ellipsis.Length;
        var left = charsToKeep / 2;
        var right = charsToKeep - left;
        return text[..left] + ellipsis + text[^right..];
    }

    private static string TrimTrailingShortId(string longName, string shortId)
    {
        var text = (longName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(shortId))
            return text;

        if (!text.EndsWith(shortId, StringComparison.OrdinalIgnoreCase))
            return text;

        var trimmed = text[..^shortId.Length].TrimEnd();
        while (trimmed.EndsWith("-", StringComparison.Ordinal) ||
               trimmed.EndsWith("_", StringComparison.Ordinal) ||
               trimmed.EndsWith(" ", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? text : trimmed;
    }

    private static bool IsValidMapPosition(double lat, double lon)
    {
        if (double.IsNaN(lat) || double.IsInfinity(lat) ||
            double.IsNaN(lon) || double.IsInfinity(lon))
            return false;

        if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            return false;

        return !(Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001);
    }

    private static bool TryGetLogKind(object sender, out LogKind kind)
    {
        if (sender is FrameworkElement element && element.Tag is string tag &&
            Enum.TryParse(tag, out LogKind parsed))
        {
            kind = parsed;
            return true;
        }

        kind = default;
        return false;
    }

    private static string GetLogFolderPath(LogKind kind)
    {
        var baseDir = AppDataPaths.LogsPath;
        return kind switch
        {
            LogKind.DeviceMetrics => Path.Combine(baseDir, "device_metrics"),
            LogKind.TraceRoute => Path.Combine(baseDir, "traceroute"),
            LogKind.EnvironmentMetrics => Path.Combine(baseDir, "environment_metrics"),
            LogKind.PowerMetrics => Path.Combine(baseDir, "power_metrics"),
            LogKind.DetectionSensor => Path.Combine(baseDir, "detection_sensor"),
            LogKind.Position => AppDataPaths.GpsPath,
            _ => baseDir
        };
    }

    private async System.Threading.Tasks.Task DeleteLogOlderAsync(LogKind kind)
    {
        switch (kind)
        {
            case LogKind.Position:
                await DeletePositionLogOlderAsync();
                break;
            case LogKind.DeviceMetrics:
                await DeleteDeviceMetricsOlderAsync();
                break;
            case LogKind.TraceRoute:
            case LogKind.EnvironmentMetrics:
            case LogKind.PowerMetrics:
            case LogKind.DetectionSensor:
                await DeleteTextLogOlderAsync(kind);
                break;
        }
    }

    private async System.Threading.Tasks.Task DeleteLogAllAsync(LogKind kind)
    {
        switch (kind)
        {
            case LogKind.Position:
                await DeletePositionLogAllAsync();
                break;
            case LogKind.DeviceMetrics:
                DeleteDeviceMetricsAll();
                break;
            case LogKind.TraceRoute:
            case LogKind.EnvironmentMetrics:
            case LogKind.PowerMetrics:
            case LogKind.DetectionSensor:
                DeleteTextLogAll(kind);
                break;
        }
    }

    private async System.Threading.Tasks.Task DeletePositionLogOlderAsync()
    {
        if (Selected is null)
            return;

        var entries = ReadPositionEntries();
        if (entries.Count == 0)
        {
            await ShowStatusAsync("No position log entries to delete.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var remaining = entries
            .Where(entry => entry.TimestampUtc >= cutoff)
            .OrderByDescending(entry => entry.TimestampUtc)
            .ToList();

        var selectedKey = _selectedPositionEntry is null
            ? null
            : new PositionLogKey(_selectedPositionEntry.TimestampUtc, _selectedPositionEntry.Lat, _selectedPositionEntry.Lon);

        var points = remaining
            .OrderBy(entry => entry.TimestampUtc)
            .Select(entry => new GpsArchive.PositionPoint(entry.Lat, entry.Lon, entry.TimestampUtc, entry.Alt, entry.Src))
            .ToList();

        GpsArchive.WriteAll(Selected.IdHex, points);

        RefreshPositionEntries(remaining);

        if (selectedKey is not null)
        {
            var restored = PositionLogEntries.FirstOrDefault(entry =>
                entry.TimestampUtc == selectedKey.TimestampUtc &&
                Math.Abs(entry.Lat - selectedKey.Lat) < 0.0000001 &&
                Math.Abs(entry.Lon - selectedKey.Lon) < 0.0000001);
            PositionLogList.SelectedItem = restored;
        }
    }

    private async System.Threading.Tasks.Task DeletePositionLogAllAsync()
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, LogKind.Position);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No position log entries to delete.");
            return;
        }

        File.Delete(path);
        PositionLogEntries.Clear();
        _selectedPositionEntry = null;
        OnChanged(nameof(HasPositionSelection));
    }

    private async System.Threading.Tasks.Task DeleteDeviceMetricsOlderAsync()
    {
        if (Selected is null)
            return;

        var path = DeviceMetricsLogService.GetLogPath(Selected.IdHex);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No device metrics log entries to delete.");
            return;
        }

        var lines = File.ReadAllLines(path);
        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var filtered = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(line);
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length == 0)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                filtered.Add(line);
                continue;
            }

            if (timestamp.ToUniversalTime() >= cutoff)
                filtered.Add(line);
        }

        DeviceMetricsLogService.ClearSamples(Selected.IdHex);

        var hasDataLines = filtered.Any(line => !line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase));
        if (hasDataLines)
        {
            if (!filtered.Any(line => line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase)))
                filtered.Insert(0, "timestamp_utc,battery_volts,battery_percent,channel_utilization,airtime,is_powered");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, filtered);
        }

        RefreshDeviceMetricsSamples();
    }

    private void DeleteDeviceMetricsAll()
    {
        if (Selected is null)
            return;

        DeviceMetricsLogService.ClearSamples(Selected.IdHex);
        _deviceMetricSamples.Clear();
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private async System.Threading.Tasks.Task DeleteTextLogOlderAsync(LogKind kind)
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, kind);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No log entries to delete.");
            return;
        }

        var lines = File.ReadAllLines(path);
        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var filtered = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(new[] { " | " }, 2, StringSplitOptions.None);
            if (parts.Length == 0)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                filtered.Add(line);
                continue;
            }

            if (timestamp.ToUniversalTime() >= cutoff)
                filtered.Add(line);
        }

        if (filtered.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, filtered);
        }

        RefreshSelectedNodeLogs();
    }

    private void DeleteTextLogAll(LogKind kind)
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, kind);
        if (File.Exists(path))
            File.Delete(path);

        RefreshSelectedNodeLogs();
    }

    private readonly record struct TraceRouteParsed(
        List<uint> Route,
        List<int> SnrTowards,
        List<uint> RouteBack,
        List<int> SnrBack,
        double? RxSnr,
        double? RxRssi,
        uint? FromNode,
        uint? ToNode,
        uint? Channel,
        string? Variant,
        bool IsPassive,
        bool IsActive,
        bool IsNoResponse);

    private enum LogKind
    {
        DeviceMetrics,
        Position,
        TraceRoute,
        EnvironmentMetrics,
        PowerMetrics,
        DetectionSensor
    }

    private static readonly LogKind[] AllLogKinds =
    [
        LogKind.DeviceMetrics,
        LogKind.Position,
        LogKind.TraceRoute,
        LogKind.EnvironmentMetrics,
        LogKind.PowerMetrics,
        LogKind.DetectionSensor
    ];

    private static NodeLogType ToArchiveType(LogKind kind)
        => kind switch
        {
            LogKind.DeviceMetrics => NodeLogType.DeviceMetrics,
            LogKind.TraceRoute => NodeLogType.TraceRoute,
            LogKind.EnvironmentMetrics => NodeLogType.EnvironmentMetrics,
            LogKind.PowerMetrics => NodeLogType.PowerMetrics,
            LogKind.DetectionSensor => NodeLogType.DetectionSensor,
            _ => NodeLogType.DeviceMetrics
        };

    private bool EnsureOnUi(Action action)
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is null)
                return false;

            if (dq.HasThreadAccess)
                return true;

            _ = dq.TryEnqueue(() => action());
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void OnChanged(string name)
    {
        if (!EnsureOnUi(() => OnChanged(name)))
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
