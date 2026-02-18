using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeshtasticWin.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsRemoteAdminPage : Page
{
    private readonly ObservableCollection<AdminTargetItem> _adminTargets = new();
    private readonly ObservableCollection<AdminTargetItem> _allAdminTargets = new();
    private bool _selectionSyncing;

    public SettingsRemoteAdminPage()
    {
        InitializeComponent();

        AdminTargetCombo.ItemsSource = _adminTargets;
        AdminTargetCombo.DisplayMemberPath = nameof(AdminTargetItem.Label);

        Loaded += SettingsRemoteAdminPage_Loaded;
        Unloaded += SettingsRemoteAdminPage_Unloaded;
    }

    private void SettingsRemoteAdminPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        AppState.ConnectedNodeChanged += AppState_ConnectedNodeChanged;
        AppState.AdminTargetChanged += AppState_AdminTargetChanged;

        HideInactiveToggle.IsChecked = AppState.HideInactiveAdminTargets;
        RebuildAdminTargets();
    }

    private void SettingsRemoteAdminPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        AppState.ConnectedNodeChanged -= AppState_ConnectedNodeChanged;
        AppState.AdminTargetChanged -= AppState_AdminTargetChanged;
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RebuildAdminTargets);

    private void AppState_ConnectedNodeChanged()
        => DispatcherQueue.TryEnqueue(RebuildAdminTargets);

    private void AppState_AdminTargetChanged()
        => DispatcherQueue.TryEnqueue(() =>
        {
            SyncSelectionFromState();
            UpdateStatusText();
        });

    private void RebuildAdminTargets()
    {
        _allAdminTargets.Clear();
        _allAdminTargets.Add(new AdminTargetItem(null, "Connected node (default)"));

        foreach (var node in AppState.Nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(node.IdHex))
                continue;

            if (AppState.HideInactiveAdminTargets && !IsOnlineByRssi(node))
                continue;

            _allAdminTargets.Add(new AdminTargetItem(node.IdHex, BuildNodeLabel(node)));
        }

        ApplyFilter();
        SyncSelectionFromState();
        UpdateStatusText();
    }

    private void ApplyFilter()
    {
        var query = (AdminTargetSearchBox.Text ?? string.Empty).Trim();

        _adminTargets.Clear();
        foreach (var item in _allAdminTargets)
        {
            if (string.IsNullOrWhiteSpace(query) || MatchesSearch(item, query))
                _adminTargets.Add(item);
        }
    }

    private void SyncSelectionFromState()
    {
        _selectionSyncing = true;
        try
        {
            var selectedId = AppState.AdminTargetNodeIdHex;
            var match = _adminTargets.FirstOrDefault(x =>
                string.Equals(x.IdHex, selectedId, StringComparison.OrdinalIgnoreCase));
            AdminTargetCombo.SelectedItem = match ?? _adminTargets.FirstOrDefault();
        }
        finally
        {
            _selectionSyncing = false;
        }
    }

    private void AdminTargetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        SyncSelectionFromState();
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        AppState.HideInactiveAdminTargets = HideInactiveToggle.IsChecked == true;
        RebuildAdminTargets();
    }

    private void AdminTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionSyncing)
            return;

        if (AdminTargetCombo.SelectedItem is not AdminTargetItem selected)
            return;

        AppState.SetAdminTargetNodeIdHex(selected.IdHex);
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (AdminTargetCombo.SelectedItem is AdminTargetItem selected)
        {
            StatusText.Text = selected.IdHex is null
                ? "Using connected node (default)."
                : $"Using admin target: {selected.Label}";
            return;
        }

        StatusText.Text = "Connected node (default)";
    }

    private static bool IsOnlineByRssi(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.RSSI) || node.RSSI == "—")
            return false;

        return int.TryParse(node.RSSI, out var rssi) && rssi != 0;
    }

    private static string BuildNodeLabel(NodeLive node)
    {
        var name = string.IsNullOrWhiteSpace(node.Name) ? "Unknown node" : node.Name.Trim();
        var shortId = string.IsNullOrWhiteSpace(node.ShortId) ? "" : $" ({node.ShortId.Trim()})";
        var idHex = string.IsNullOrWhiteSpace(node.IdHex) ? "" : $" - {node.IdHex.Trim()}";
        return $"{name}{shortId}{idHex}";
    }

    private static bool MatchesSearch(AdminTargetItem item, string query)
    {
        if (item.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(item.IdHex) && item.IdHex.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private sealed record AdminTargetItem(string? IdHex, string Label);
}
