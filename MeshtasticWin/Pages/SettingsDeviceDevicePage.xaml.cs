using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsDeviceDevicePage : Page
{
    public SettingsDeviceDevicePage()
    {
        InitializeComponent();
        Loaded += SettingsDeviceDevicePage_Loaded;
        RoleCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.DeviceConfig.Types.Role>();
        RebroadcastCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.DeviceConfig.Types.RebroadcastMode>();
    }

    private async void SettingsDeviceDevicePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit device settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading device configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DeviceConfig);
            var device = config.Device ?? new Config.Types.DeviceConfig();

            RoleCombo.SelectedItem = device.Role;
            RebroadcastCombo.SelectedItem = device.RebroadcastMode;
            NodeInfoSecsBox.Text = SettingsConfigUiUtil.UIntText(device.NodeInfoBroadcastSecs);
            DoubleTapToggle.IsOn = device.DoubleTapAsButtonPress;
            TripleClickToggle.IsOn = !device.DisableTripleClick;
            LedHeartbeatToggle.IsOn = !device.LedHeartbeatDisabled;
            TimezoneBox.Text = device.Tzdef ?? string.Empty;
            ButtonGpioBox.Text = SettingsConfigUiUtil.UIntText(device.ButtonGpio);
            BuzzerGpioBox.Text = SettingsConfigUiUtil.UIntText(device.BuzzerGpio);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load device configuration: " + ex.Message;
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

        if (RoleCombo.SelectedItem is not Config.Types.DeviceConfig.Types.Role role)
        {
            StatusText.Text = "Select a device role.";
            return;
        }

        if (RebroadcastCombo.SelectedItem is not Config.Types.DeviceConfig.Types.RebroadcastMode rebroadcast)
        {
            StatusText.Text = "Select a rebroadcast mode.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(NodeInfoSecsBox.Text, out var nodeInfoSecs) ||
            !SettingsConfigUiUtil.TryParseUInt(ButtonGpioBox.Text, out var buttonGpio) ||
            !SettingsConfigUiUtil.TryParseUInt(BuzzerGpioBox.Text, out var buzzerGpio))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var device = new Config.Types.DeviceConfig
            {
                Role = role,
                RebroadcastMode = rebroadcast,
                NodeInfoBroadcastSecs = nodeInfoSecs,
                DoubleTapAsButtonPress = DoubleTapToggle.IsOn,
                DisableTripleClick = !TripleClickToggle.IsOn,
                LedHeartbeatDisabled = !LedHeartbeatToggle.IsOn,
                Tzdef = (TimezoneBox.Text ?? string.Empty).Trim(),
                ButtonGpio = buttonGpio,
                BuzzerGpio = buzzerGpio
            };

            StatusText.Text = "Saving device configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Device = device });
            StatusText.Text = "Device configuration saved.";
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
                    ? "Device configuration saved. Reconnected."
                    : "Device configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save device configuration: " + ex.Message;
        }
    }
}
