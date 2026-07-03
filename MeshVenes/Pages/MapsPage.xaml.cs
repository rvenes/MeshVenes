using MeshVenes.Models;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.ApplicationModel;
using SortMode = MeshVenes.Services.ListSortMode;

namespace MeshVenes.Pages;

public sealed partial class MapsPage : Page, INotifyPropertyChanged
{
    private const string LastSelectedNodeKey = "NodesLastSelectedNodeIdHex";
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NodeLive> VisibleNodes { get; } = new();

    private readonly ObservableCollection<NodeLive> _allNodes = new();
    private readonly DispatcherTimer _filterApplyTimer = new();
    private readonly DispatcherTimer _mapUpdateTimer = new();
    private bool _hideInactive = true;
    private bool _filterMyNodesOnly;
    private bool _filterFavoritesOnly;
    private bool _includeLoRa = true;
    private bool _includeMqtt = true;
    private string _filter = "";
    private SortMode _sortMode = SortMode.LastHeard;
    private bool _sortDescending = true;
    private bool _mapReady;
    private bool _mapConfigured;
    private bool _mapEventsAttached;
    private bool _pendingAutoFitOnLoad = true;
    private System.Threading.Tasks.Task? _mapInitializationTask;
    private NodeLive? _selected;

    public MapsPage()
    {
        InitializeComponent();

        _filterApplyTimer.Interval = TimeSpan.FromMilliseconds(250);
        _filterApplyTimer.Tick += (_, __) =>
        {
            _filterApplyTimer.Stop();
            RebuildVisibleNodes();
        };

        _mapUpdateTimer.Interval = TimeSpan.FromMilliseconds(300);
        _mapUpdateTimer.Tick += async (_, __) =>
        {
            _mapUpdateTimer.Stop();
            await PushAllNodesToMapAsync();
            await PushWaypointsToMapAsync();
        };

        foreach (var node in AppState.Nodes)
        {
            node.PropertyChanged += Node_PropertyChanged;
            _allNodes.Add(node);
        }

        AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        AppState.Waypoints.CollectionChanged += Waypoints_CollectionChanged;
        foreach (var waypoint in AppState.Waypoints)
            waypoint.PropertyChanged += Waypoint_PropertyChanged;

        RebuildVisibleNodes();
        Loaded += MapsPage_Loaded;
        Unloaded += MapsPage_Unloaded;
    }

    public bool HasSelection => _selected is not null;
    public bool IsSortByAlphabetical => _sortMode == SortMode.Alphabetical;
    public bool IsSortByHopsAway => _sortMode == SortMode.HopsAway;
    public bool IsSortByLastHeard => _sortMode == SortMode.LastHeard;
    public bool IsSortByFavorites => _sortMode == SortMode.FavoritesFirst;
    public bool IsSortByMyNodes => _sortMode == SortMode.MyNodes;
    public bool IsSortAscending => !_sortDescending;
    public bool IsSortDescending => _sortDescending;
    public bool IsHideInactiveEnabled => _hideInactive;
    public bool IsMyNodesFilterEnabled => _filterMyNodesOnly;
    public bool IsFavoritesFilterEnabled => _filterFavoritesOnly;
    public bool IsLoRaFilterEnabled => _includeLoRa;
    public bool IsMqttFilterEnabled => _includeMqtt;
    public string ActiveMapFiltersText => BuildMapFiltersText();
    public Visibility ActiveMapFiltersVisibility => HasMapFilters() ? Visibility.Visible : Visibility.Collapsed;

    public string NodeCountsText
    {
        get
        {
            var total = AppState.Nodes.Count;
            var online = AppState.Nodes.Count(IsOnlineByRssi);
            return $"Online: {online}   Total: {total}";
        }
    }

    private async void MapsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureMapAsync();
        await PushAllNodesToMapAsync();
        await PushWaypointsToMapAsync();
        await PushSelectionToMapAsync();
        TryAutoFitMapOnLoad();
    }

    private void MapsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        AppState.Waypoints.CollectionChanged -= Waypoints_CollectionChanged;
        foreach (var node in _allNodes)
            node.PropertyChanged -= Node_PropertyChanged;
        foreach (var waypoint in AppState.Waypoints)
            waypoint.PropertyChanged -= Waypoint_PropertyChanged;

        if (_mapEventsAttached && MapView.CoreWebView2 is not null)
        {
            MapView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            MapView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            _mapEventsAttached = false;
        }
    }

    private async System.Threading.Tasks.Task EnsureMapAsync()
    {
        if (_mapConfigured)
            return;

        _mapInitializationTask ??= InitializeMapAsync();
        await _mapInitializationTask;
    }

    private async System.Threading.Tasks.Task InitializeMapAsync()
    {
        try
        {
            await MapView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            ShowMapFallback($"Map initialization failed: {ex.Message}");
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

        var mapFolderPath = Path.GetFullPath(Path.Combine(ResolveInstallPath(), "Assets"));
        var mapHtml = Path.Combine(mapFolderPath, "Map", "map.html");
        if (!File.Exists(mapHtml))
        {
            ShowMapFallback("Map HTML missing.");
            _mapInitializationTask = null;
            return;
        }

        try
        {
            _mapReady = false;
            wv.SetVirtualHostNameToFolderMapping("mapassets.local", mapFolderPath, CoreWebView2HostResourceAccessKind.Allow);
            MapView.Source = new Uri("https://mapassets.local/Map/map.html");
            HideMapFallback();
            _mapConfigured = true;
        }
        catch (Exception ex)
        {
            ShowMapFallback($"Map navigation setup failed: {ex.Message}");
            _mapInitializationTask = null;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(type))
                return;

            if (type == "ready")
            {
                _mapReady = true;
                _ = PushAllNodesToMapAsync();
                _ = PushWaypointsToMapAsync();
                _ = PushSelectionToMapAsync();
                TryAutoFitMapOnLoad();
                return;
            }

            if (type == "selectNode")
            {
                var id = root.TryGetProperty("idHex", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    return;

                var node = AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, id, StringComparison.OrdinalIgnoreCase));
                if (node is null)
                    return;

                NodesList.SelectedItem = node;
                _selected = node;
                NodesList.ScrollIntoView(node);
                OnChanged(nameof(HasSelection));
            }
        }
        catch
        {
            // Ignore malformed optional map messages.
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            HideMapFallback();
            _ = MapView.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            return;
        }

        _mapReady = false;
        _mapConfigured = false;
        _mapInitializationTask = null;
        ShowMapFallback($"Map failed to load: {e.WebErrorStatus}");
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (NodeLive node in e.NewItems)
            {
                node.PropertyChanged += Node_PropertyChanged;
                _allNodes.Add(node);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (NodeLive node in e.OldItems)
            {
                node.PropertyChanged -= Node_PropertyChanged;
                _allNodes.Remove(node);
            }
        }

        ScheduleFilterApply();
        OnChanged(nameof(NodeCountsText));
        TriggerMapUpdate();
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NodeLive node)
            return;

        if (ShouldRefreshMapFilter(e.PropertyName, node))
        {
            ScheduleFilterApply();
            TriggerMapUpdate();
        }
        else if (e.PropertyName is nameof(NodeLive.RSSI) or nameof(NodeLive.SNR) or nameof(NodeLive.ViaMqtt))
        {
            TriggerMapUpdate();
        }

        if (ShouldRefreshMapSorting(e.PropertyName))
            RebuildVisibleNodes();

        OnChanged(nameof(NodeCountsText));
    }

    private void Waypoints_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (WaypointLive waypoint in e.NewItems)
                waypoint.PropertyChanged += Waypoint_PropertyChanged;

        if (e.OldItems is not null)
            foreach (WaypointLive waypoint in e.OldItems)
                waypoint.PropertyChanged -= Waypoint_PropertyChanged;

        TriggerMapUpdate();
    }

    private void Waypoint_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        => TriggerMapUpdate();

    private void ScheduleFilterApply()
    {
        if (_filterApplyTimer.IsEnabled)
            _filterApplyTimer.Stop();
        _filterApplyTimer.Start();
    }

    private void TriggerMapUpdate()
    {
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        if (_mapUpdateTimer.IsEnabled)
            _mapUpdateTimer.Stop();
        _mapUpdateTimer.Start();
    }

    private void RebuildVisibleNodes()
    {
        _filterApplyTimer.Stop();

        var desired = SortNodes(_allNodes.Where(ShouldShowNode).ToList());

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

        if (_selected is not null && !VisibleNodes.Contains(_selected))
            _selected = null;

        OnChanged(nameof(NodeCountsText));
        OnChanged(nameof(HasSelection));
        TriggerMapUpdate();
    }

    private bool ShouldShowNode(NodeLive node)
    {
        if (IsHiddenByInactive(node))
            return false;

        if (!MatchesNodeCategoryFilter(node) || !MatchesNodeTransportFilter(node))
            return false;

        var q = (_filter ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return (node.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.IdHex?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.ShortId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private List<NodeLive> SortNodes(List<NodeLive> nodes)
        => ListSorter.Sort(
            nodes,
            _sortMode,
            _sortDescending,
            n => n.LastHeardUtc,
            n => n.HopsAway,
            n => n.IsFavorite,
            n => n.IsMyNode,
            n => n.SortNameKey,
            n => n.SortIdKey);

    private bool IsHiddenByInactive(NodeLive node)
    {
        if (node.IsFavorite || node.IsMyNode)
            return false;
        if (!_hideInactive)
            return false;
        if (node.LastHeardUtc == DateTime.MinValue)
            return true;
        return !IsOnlineByRssi(node);
    }

    private bool MatchesNodeCategoryFilter(NodeLive node)
    {
        if (!_filterMyNodesOnly && !_filterFavoritesOnly)
            return true;

        return (_filterMyNodesOnly && node.IsMyNode)
            || (_filterFavoritesOnly && node.IsFavorite);
    }

    private bool MatchesNodeTransportFilter(NodeLive node)
    {
        if (!HasNodeTransportFilter())
            return true;

        var viaMqtt = node.ViaMqtt == true;
        return (viaMqtt && _includeMqtt) || (!viaMqtt && _includeLoRa);
    }

    private bool HasMapFilters()
        => _hideInactive
            || _filterMyNodesOnly
            || _filterFavoritesOnly
            || HasNodeTransportFilter()
            || !string.IsNullOrWhiteSpace(_filter);

    private bool HasNodeTransportFilter()
        => _includeLoRa != _includeMqtt;

    private bool ShouldRefreshMapFilter(string? propertyName, NodeLive node)
    {
        if (propertyName is nameof(NodeLive.Name) or nameof(NodeLive.ShortName) or nameof(NodeLive.SortNameKey)
            or nameof(NodeLive.IsFavorite) or nameof(NodeLive.IsMyNode)
            or nameof(NodeLive.Latitude) or nameof(NodeLive.Longitude) or nameof(NodeLive.LastPositionUtc))
        {
            return true;
        }

        if (propertyName is nameof(NodeLive.RSSI) or nameof(NodeLive.ViaMqtt))
            return HasMapFilters() || !VisibleNodes.Contains(node);

        if (propertyName is nameof(NodeLive.LastHeardUtc) or nameof(NodeLive.LastHeard))
            return _hideInactive && !VisibleNodes.Contains(node);

        return false;
    }

    private bool ShouldRefreshMapSorting(string? propertyName)
    {
        return propertyName switch
        {
            nameof(NodeLive.Name) or nameof(NodeLive.ShortName) or nameof(NodeLive.SortNameKey)
                => _sortMode == SortMode.Alphabetical,
            nameof(NodeLive.HopsAway)
                => _sortMode == SortMode.HopsAway,
            nameof(NodeLive.IsFavorite)
                => _sortMode == SortMode.FavoritesFirst,
            nameof(NodeLive.IsMyNode)
                => _sortMode == SortMode.MyNodes,
            _ => false
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        RefreshSortFilterUi();
        RebuildVisibleNodes();
    }

    private void SortModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.Tag is not string tag)
            return;

        if (!Enum.TryParse<SortMode>(tag, ignoreCase: true, out var nextMode) || _sortMode == nextMode)
            return;

        _sortMode = nextMode;
        RefreshSortFilterUi();
        RebuildVisibleNodes();
    }

    private void SortDirectionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.Tag is not string tag)
            return;

        var nextDescending = string.Equals(tag, "Descending", StringComparison.OrdinalIgnoreCase);
        if (_sortDescending == nextDescending)
            return;

        _sortDescending = nextDescending;
        RefreshSortFilterUi();
        RebuildVisibleNodes();
    }

    private void FilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string tag)
            return;

        var isChecked = checkBox.IsChecked == true;
        var changed = tag switch
        {
            "HideInactive" when _hideInactive != isChecked => SetField(ref _hideInactive, isChecked),
            "MyNodesOnly" when _filterMyNodesOnly != isChecked => SetField(ref _filterMyNodesOnly, isChecked),
            "FavoritesOnly" when _filterFavoritesOnly != isChecked => SetField(ref _filterFavoritesOnly, isChecked),
            "IncludeLoRa" when _includeLoRa != isChecked => SetField(ref _includeLoRa, isChecked),
            "IncludeMqtt" when _includeMqtt != isChecked => SetField(ref _includeMqtt, isChecked),
            _ => false
        };

        if (!changed)
            return;

        RefreshSortFilterUi();
        RebuildVisibleNodes();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = false;
        _filterMyNodesOnly = false;
        _filterFavoritesOnly = false;
        _includeLoRa = true;
        _includeMqtt = true;
        _filter = "";
        SearchBox.Text = "";

        RefreshSortFilterUi();
        RebuildVisibleNodes();
    }

    private void RefreshSortFilterUi()
    {
        OnChanged(nameof(IsSortByAlphabetical));
        OnChanged(nameof(IsSortByHopsAway));
        OnChanged(nameof(IsSortByLastHeard));
        OnChanged(nameof(IsSortByFavorites));
        OnChanged(nameof(IsSortByMyNodes));
        OnChanged(nameof(IsSortAscending));
        OnChanged(nameof(IsSortDescending));
        OnChanged(nameof(IsHideInactiveEnabled));
        OnChanged(nameof(IsMyNodesFilterEnabled));
        OnChanged(nameof(IsFavoritesFilterEnabled));
        OnChanged(nameof(IsLoRaFilterEnabled));
        OnChanged(nameof(IsMqttFilterEnabled));
        OnChanged(nameof(ActiveMapFiltersText));
        OnChanged(nameof(ActiveMapFiltersVisibility));
    }

    private void NodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = NodesList.SelectedItem as NodeLive;
        OnChanged(nameof(HasSelection));
        _ = PushSelectionToMapAsync();
    }

    private void NodesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => ZoomToSelected();

    private void ZoomToSelected_Click(object sender, RoutedEventArgs e)
        => ZoomToSelected();

    private void SendDm_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        AppState.SetActiveChatPeer(_selected.IdHex);
        App.MainWindowInstance?.NavigateTo("messages");
    }

    private void OpenNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        SettingsStore.SetString(LastSelectedNodeKey, _selected.IdHex);
        App.MainWindowInstance?.NavigateTo("nodes");
    }

    private void ZoomToSelected()
    {
        if (_selected is null || !_mapReady || MapView.CoreWebView2 is null)
            return;

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "zoomTo", idHex = _selected.IdHex }, s_jsonOptions));
    }

    private void FitAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fitAll\"}");
    }

    private void ResetMap_Click(object sender, RoutedEventArgs e)
    {
        _pendingAutoFitOnLoad = true;
        FitAll_Click(sender, e);
    }

    private async System.Threading.Tasks.Task PushAllNodesToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        var nodes = VisibleNodes
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
        if (!_mapReady || MapView.CoreWebView2 is null)
            return;

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "selected", idHex = _selected?.IdHex }, s_jsonOptions));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void TryAutoFitMapOnLoad()
    {
        if (!_pendingAutoFitOnLoad || !_mapReady || MapView.CoreWebView2 is null)
            return;

        _pendingAutoFitOnLoad = false;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fitAll\"}");
    }

    private string BuildMapFiltersText()
    {
        var filters = new List<string>();
        if (_hideInactive)
            filters.Add("Hide inactive");
        if (_filterMyNodesOnly)
            filters.Add("My Nodes");
        if (_filterFavoritesOnly)
            filters.Add("Favorites");
        if (HasNodeTransportFilter())
            filters.Add(_includeMqtt ? "Via MQTT" : "Via LoRa");
        if (!string.IsNullOrWhiteSpace(_filter))
            filters.Add($"Search: {_filter.Trim()}");

        return filters.Count == 0 ? "" : $"Filters: {string.Join(", ", filters)}";
    }

    private void ShowMapFallback(string message)
    {
        MapFallbackText.Text = message;
        MapFallbackText.Visibility = Visibility.Visible;
    }

    private void HideMapFallback()
        => MapFallbackText.Visibility = Visibility.Collapsed;

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
        => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsOnlineByRssi(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.RSSI) || node.RSSI == "—")
            return false;
        return int.TryParse(node.RSSI, out var rssi) && rssi != 0;
    }

    private static bool IsValidMapPosition(double lat, double lon)
        => !double.IsNaN(lat)
            && !double.IsNaN(lon)
            && Math.Abs(lat) <= 90
            && Math.Abs(lon) <= 180
            && !(Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001);

    private static bool SetField(ref bool field, bool value)
    {
        if (field == value)
            return false;

        field = value;
        return true;
    }

    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
