using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsDeviceNetworkPage : Page
{
    private const uint UdpBroadcastFlag = (uint)Config.Types.NetworkConfig.Types.ProtocolFlags.UdpBroadcast;

    public SettingsDeviceNetworkPage()
    {
        InitializeComponent();
        Loaded += SettingsDeviceNetworkPage_Loaded;
    }

    private async void SettingsDeviceNetworkPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit network settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading network configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.NetworkConfig);
            var network = config.Network ?? new Config.Types.NetworkConfig();

            WifiEnabledToggle.IsOn = network.WifiEnabled;
            WifiSsidBox.Text = network.WifiSsid ?? string.Empty;
            WifiPasswordBox.Text = network.WifiPsk ?? string.Empty;
            NtpServerBox.Text = network.NtpServer ?? string.Empty;
            UdpBroadcastToggle.IsOn = (network.EnabledProtocols & UdpBroadcastFlag) != 0;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load network configuration: " + ex.Message;
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before saving.";
            return;
        }

        try
        {
            uint enabledProtocols = 0;
            if (UdpBroadcastToggle.IsOn)
                enabledProtocols |= UdpBroadcastFlag;

            var network = new Config.Types.NetworkConfig
            {
                WifiEnabled = WifiEnabledToggle.IsOn,
                WifiSsid = (WifiSsidBox.Text ?? string.Empty).Trim(),
                WifiPsk = (WifiPasswordBox.Text ?? string.Empty).Trim(),
                NtpServer = (NtpServerBox.Text ?? string.Empty).Trim(),
                EnabledProtocols = enabledProtocols
            };

            StatusText.Text = "Saving network configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Network = network });
            StatusText.Text = "Network configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(
                text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(
                    text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Network configuration saved. Reconnected."
                    : "Network configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save network configuration: " + ex.Message;
        }
    }
}
