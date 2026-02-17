using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsDeviceBluetoothPage : Page
{
    public SettingsDeviceBluetoothPage()
    {
        InitializeComponent();
        Loaded += SettingsDeviceBluetoothPage_Loaded;
        ModeCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.BluetoothConfig.Types.PairingMode>();
    }

    private async void SettingsDeviceBluetoothPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit bluetooth settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading bluetooth configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.BluetoothConfig);
            var bluetooth = config.Bluetooth ?? new Config.Types.BluetoothConfig();
            EnabledToggle.IsOn = bluetooth.Enabled;
            ModeCombo.SelectedItem = bluetooth.Mode;
            FixedPinBox.Text = SettingsConfigUiUtil.UIntText(bluetooth.FixedPin);
            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load bluetooth configuration: " + ex.Message;
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

        if (ModeCombo.SelectedItem is not Config.Types.BluetoothConfig.Types.PairingMode mode)
        {
            StatusText.Text = "Select a pairing mode.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(FixedPinBox.Text, out var fixedPin))
        {
            StatusText.Text = "Fixed PIN must be an unsigned number.";
            return;
        }

        try
        {
            var bluetooth = new Config.Types.BluetoothConfig
            {
                Enabled = EnabledToggle.IsOn,
                Mode = mode,
                FixedPin = fixedPin
            };

            StatusText.Text = "Saving bluetooth configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Bluetooth = bluetooth });
            StatusText.Text = "Bluetooth configuration saved.";
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
                    ? "Bluetooth configuration saved. Reconnected."
                    : "Bluetooth configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save bluetooth configuration: " + ex.Message;
        }
    }
}
