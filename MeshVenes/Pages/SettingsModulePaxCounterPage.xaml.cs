using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModulePaxCounterPage : Page
{
    public SettingsModulePaxCounterPage()
    {
        InitializeComponent();
        Loaded += SettingsModulePaxCounterPage_Loaded;
    }

    private async void SettingsModulePaxCounterPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit PAX counter settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading PAX counter configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.PaxcounterConfig);
            var config = module.Paxcounter ?? new ModuleConfig.Types.PaxcounterConfig();

            EnabledToggle.IsOn = config.Enabled;
            UpdateIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.PaxcounterUpdateInterval);
            WifiThresholdBox.Text = SettingsConfigUiUtil.IntText(config.WifiThreshold);
            BleThresholdBox.Text = SettingsConfigUiUtil.IntText(config.BleThreshold);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load PAX counter configuration: " + ex.Message;
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

        if (!SettingsConfigUiUtil.TryParseUInt(UpdateIntervalBox.Text, out var updateInterval) ||
            !SettingsConfigUiUtil.TryParseInt(WifiThresholdBox.Text, out var wifiThreshold) ||
            !SettingsConfigUiUtil.TryParseInt(BleThresholdBox.Text, out var bleThreshold))
        {
            StatusText.Text = "Invalid numeric value.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.PaxcounterConfig
            {
                Enabled = EnabledToggle.IsOn,
                PaxcounterUpdateInterval = updateInterval,
                WifiThreshold = wifiThreshold,
                BleThreshold = bleThreshold
            };

            StatusText.Text = "Saving PAX counter configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { Paxcounter = config });
            StatusText.Text = "PAX counter configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "PAX counter configuration saved. Reconnected."
                    : "PAX counter configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save PAX counter configuration: " + ex.Message;
        }
    }
}
