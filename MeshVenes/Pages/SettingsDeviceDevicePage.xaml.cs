using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

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

    private async void ResetNodeDb_Click(object sender, RoutedEventArgs e)
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before resetting NodeDB.";
            return;
        }

        var confirmed = await ConfirmActionAsync(
            "Reset NodeDB",
            "Are you sure you want to reset NodeDB on this node? Favorites are preserved.",
            "Reset NodeDB");
        if (!confirmed)
            return;

        try
        {
            StatusText.Text = "Resetting NodeDB...";
            await AdminConfigClient.Instance.ResetNodeDbAsync(nodeNum);
            StatusText.Text = "NodeDB reset command sent. Node may reboot.";
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
                    ? "NodeDB reset command sent. Reconnected."
                    : "NodeDB reset may be applied, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to reset NodeDB: " + ex.Message;
        }
    }

    private async void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before factory reset.";
            return;
        }

        var confirmed = await ConfirmActionAsync(
            "Factory Reset",
            "Are you sure you want to factory reset this node configuration?",
            "Factory Reset");
        if (!confirmed)
            return;

        try
        {
            StatusText.Text = "Sending factory reset command...";
            await AdminConfigClient.Instance.FactoryResetConfigAsync(nodeNum);
            StatusText.Text = "Factory reset command sent. Node may reboot.";
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
                    ? "Factory reset command sent. Reconnected."
                    : "Factory reset may be applied, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to send factory reset: " + ex.Message;
        }
    }

    private async Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText)
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
}
