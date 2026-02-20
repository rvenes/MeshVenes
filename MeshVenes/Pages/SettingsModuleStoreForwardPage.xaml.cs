using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleStoreForwardPage : Page
{
    public SettingsModuleStoreForwardPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleStoreForwardPage_Loaded;
    }

    private async void SettingsModuleStoreForwardPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit store & forward settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading store & forward configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.StoreforwardConfig);
            var config = module.StoreForward ?? new ModuleConfig.Types.StoreForwardConfig();

            EnabledToggle.IsOn = config.Enabled;
            IsServerToggle.IsOn = config.IsServer;
            HeartbeatToggle.IsOn = config.Heartbeat;
            RecordsBox.Text = SettingsConfigUiUtil.UIntText(config.Records);
            HistoryMaxBox.Text = SettingsConfigUiUtil.UIntText(config.HistoryReturnMax);
            HistoryWindowBox.Text = SettingsConfigUiUtil.UIntText(config.HistoryReturnWindow);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load store & forward configuration: " + ex.Message;
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before saving.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(RecordsBox.Text, out var records) ||
            !SettingsConfigUiUtil.TryParseUInt(HistoryMaxBox.Text, out var historyMax) ||
            !SettingsConfigUiUtil.TryParseUInt(HistoryWindowBox.Text, out var historyWindow))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.StoreForwardConfig
            {
                Enabled = EnabledToggle.IsOn,
                IsServer = IsServerToggle.IsOn,
                Heartbeat = HeartbeatToggle.IsOn,
                Records = records,
                HistoryReturnMax = historyMax,
                HistoryReturnWindow = historyWindow
            };

            StatusText.Text = "Saving store & forward configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { StoreForward = config });
            StatusText.Text = "Store & forward configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Store & forward configuration saved. Reconnected."
                    : "Store & forward configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save store & forward configuration: " + ex.Message;
        }
    }
}
