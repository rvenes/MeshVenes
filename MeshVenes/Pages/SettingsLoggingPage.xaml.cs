using AppDataPaths = MeshVenes.Services.AppDataPaths;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.System;

namespace MeshVenes.Pages;

public sealed partial class SettingsLoggingPage : Page
{
    private readonly ObservableCollection<string> _filtered = new();
    private readonly Dictionary<string, bool> _categoryStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _hooked;
    private bool _buildingCategoryUi;

    public SettingsLoggingPage()
    {
        InitializeComponent();
        InfoCheckBox.IsChecked = true;
        WarningCheckBox.IsChecked = true;
        ErrorCheckBox.IsChecked = true;
        DebugCheckBox.IsChecked = true;
        Loaded += SettingsLoggingPage_Loaded;
        Unloaded += SettingsLoggingPage_Unloaded;
        LogList.ItemsSource = _filtered;
    }

    private void SettingsLoggingPage_Loaded(object sender, RoutedEventArgs e)
    {
        Hook();
        RefreshFiltered();
    }

    private void SettingsLoggingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Unhook();
    }

    private void Hook()
    {
        if (_hooked)
            return;

        _hooked = true;
        RadioClient.Instance.LogLines.CollectionChanged += LogLines_CollectionChanged;
    }

    private void Unhook()
    {
        if (!_hooked)
            return;

        _hooked = false;
        RadioClient.Instance.LogLines.CollectionChanged -= LogLines_CollectionChanged;
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var dq = DispatcherQueue;
        if (dq is null)
            return;

        if (dq.HasThreadAccess)
        {
            RefreshFiltered();
            return;
        }

        _ = dq.TryEnqueue(RefreshFiltered);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFiltered();
    private void LevelFilter_Changed(object sender, RoutedEventArgs e) => RefreshFiltered();

    private void RefreshFiltered()
    {
        if (SearchBox is null || LogList is null || CountText is null)
            return;

        var q = (SearchBox.Text ?? string.Empty).Trim();
        var all = RadioClient.Instance.LogLines;
        BuildCategoryFilterUi(all);

        _filtered.Clear();
        foreach (var line in all)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!string.IsNullOrWhiteSpace(q) && !line.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            var level = DetectLevel(line);
            if (!IsLevelEnabled(level))
                continue;

            var category = DetectCategory(line);
            if (_categoryStates.TryGetValue(category, out var enabled) && !enabled)
                continue;

            _filtered.Add(line);
        }

        CountText.Text = $"Visible: {_filtered.Count}  Total: {all.Count}";
    }

    private static string DetectLevel(string line)
    {
        var t = line.ToLowerInvariant();

        if (t.Contains("exception", StringComparison.Ordinal) ||
            t.Contains(" failed", StringComparison.Ordinal) ||
            t.Contains("error", StringComparison.Ordinal))
            return "Error";

        if (t.Contains("warning", StringComparison.Ordinal) ||
            t.Contains("warn", StringComparison.Ordinal) ||
            t.Contains("tx dropped", StringComparison.Ordinal))
            return "Warning";

        if (t.Contains("debug", StringComparison.Ordinal))
            return "Debug";

        return "Info";
    }

    private bool IsLevelEnabled(string level)
        => level switch
        {
            "Info" => InfoCheckBox?.IsChecked == true,
            "Warning" => WarningCheckBox?.IsChecked == true,
            "Error" => ErrorCheckBox?.IsChecked == true,
            "Debug" => DebugCheckBox?.IsChecked == true,
            _ => true
        };

    private static string DetectCategory(string line)
    {
        var t = line.ToLowerInvariant();

        if (t.StartsWith("tx ", StringComparison.Ordinal) && t.EndsWith(" bytes", StringComparison.Ordinal)) return "TX Bytes";
        if (t.StartsWith("rx ", StringComparison.Ordinal) && t.EndsWith(" bytes", StringComparison.Ordinal)) return "RX Bytes";
        if (t.StartsWith("admin:", StringComparison.Ordinal) || t.Contains("getmoduleconfigresponse", StringComparison.Ordinal) || t.Contains("getconfigresponse", StringComparison.Ordinal)) return "Admin";
        if (t.Contains("telemetry", StringComparison.Ordinal)) return "Telemetry";
        if (t.Contains("mesh statistics", StringComparison.Ordinal) || t.Contains("airtime", StringComparison.Ordinal)) return "Stats";
        if (t.Contains("position", StringComparison.Ordinal) || t.Contains("gps", StringComparison.Ordinal)) return "Position";
        if (t.Contains("trace route", StringComparison.Ordinal) || t.Contains("traceroute", StringComparison.Ordinal)) return "TraceRoute";
        if (t.Contains("mqtt", StringComparison.Ordinal)) return "MQTT";
        if (t.Contains("bluetooth", StringComparison.Ordinal) || t.Contains(" ble", StringComparison.Ordinal)) return "BLE";
        if (t.Contains("tcp", StringComparison.Ordinal)) return "TCP";
        if (t.Contains("serial", StringComparison.Ordinal) || t.Contains("com", StringComparison.Ordinal)) return "Serial";
        if (t.Contains("transport", StringComparison.Ordinal)) return "Transport";
        if (t.Contains("message", StringComparison.Ordinal) || t.Contains("dm ", StringComparison.Ordinal) || t.Contains("chat", StringComparison.Ordinal)) return "Messages";
        if (t.Contains("[app]", StringComparison.Ordinal) || t.Contains("routed to", StringComparison.Ordinal) || t.Contains("scene ", StringComparison.Ordinal)) return "App";
        if (t.Contains("reconnect", StringComparison.Ordinal)) return "Reconnect";
        if (t.Contains("saved", StringComparison.Ordinal) || t.Contains("archive", StringComparison.Ordinal)) return "Data";
        if (t.Contains("mesh packet", StringComparison.Ordinal) || t.Contains("routing", StringComparison.Ordinal)) return "Mesh";
        return "System";
    }

    private void BuildCategoryFilterUi(IEnumerable<string> lines)
    {
        if (CategoryFiltersPanel is null)
            return;

        var categories = lines
            .Select(DetectCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (categories.Count == 0)
            categories.Add("System");

        foreach (var category in categories)
        {
            if (!_categoryStates.ContainsKey(category))
                _categoryStates[category] = true;
        }

        var stale = _categoryStates.Keys
            .Where(k => !categories.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in stale)
            _categoryStates.Remove(key);

        _buildingCategoryUi = true;
        try
        {
            CategoryFiltersPanel.Children.Clear();
            foreach (var category in categories)
            {
                var checkBox = new CheckBox
                {
                    Content = category,
                    IsChecked = _categoryStates.TryGetValue(category, out var enabled) ? enabled : true
                };
                checkBox.Checked += CategoryFilter_Changed;
                checkBox.Unchecked += CategoryFilter_Changed;
                CategoryFiltersPanel.Children.Add(checkBox);
            }
        }
        finally
        {
            _buildingCategoryUi = false;
        }
    }

    private void CategoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_buildingCategoryUi)
            return;

        if (sender is CheckBox checkBox && checkBox.Content is string category)
            _categoryStates[category] = checkBox.IsChecked == true;

        RefreshFiltered();
    }

    private void SelectAllCategories_Click(object sender, RoutedEventArgs e)
    {
        foreach (var key in _categoryStates.Keys.ToList())
            _categoryStates[key] = true;

        RefreshFiltered();
    }

    private void ClearAllCategories_Click(object sender, RoutedEventArgs e)
    {
        foreach (var key in _categoryStates.Keys.ToList())
            _categoryStates[key] = false;

        RefreshFiltered();
    }

    private async void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppDataPaths.DebugLogsRootPath, AppDataPaths.ActiveNodeScope);
            Directory.CreateDirectory(path);
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            _ = await Launcher.LaunchFolderAsync(folder);
        }
        catch
        {
            // Ignore launcher failures.
        }
    }

    private void SaveDebugLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var debugDir = Path.Combine(AppDataPaths.DebugLogsRootPath, AppDataPaths.ActiveNodeScope);
            Directory.CreateDirectory(debugDir);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var path = Path.Combine(debugDir, $"debug_{stamp}.log");
            var snapshot = RadioClient.Instance.LogLines.ToArray();
            File.WriteAllLines(path, snapshot);
            RadioClient.Instance.AddSystemLog($"Debug log saved: {path}");
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddSystemLog($"Failed to save debug log: {ex.Message}");
        }
    }

    private void CopyVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_filtered.Count == 0)
            return;

        var text = string.Join(Environment.NewLine, _filtered);
        _ = ClipboardUtil.TrySetText(text);
    }

    private void ClearLiveList_Click(object sender, RoutedEventArgs e)
    {
        RadioClient.Instance.LogLines.Clear();
        RefreshFiltered();
    }

    private void LogList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is string line)
            LogList.SelectedItem = line;
    }

    private void LogListFlyout_Opening(object sender, object e)
    {
        CopyLogMenuItem.IsEnabled = LogList.SelectedItem is string;
    }

    private void CopyLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not string line || string.IsNullOrWhiteSpace(line))
            return;

        _ = ClipboardUtil.TrySetText(line);
    }
}
