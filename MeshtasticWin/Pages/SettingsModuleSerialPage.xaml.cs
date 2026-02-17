using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsModuleSerialPage : Page
{
    public SettingsModuleSerialPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleSerialPage_Loaded;
        BaudCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<ModuleConfig.Types.SerialConfig.Types.Serial_Baud>();
        ModeCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<ModuleConfig.Types.SerialConfig.Types.Serial_Mode>();
    }

    private async void SettingsModuleSerialPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit serial settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading serial configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.SerialConfig);
            var config = module.Serial ?? new ModuleConfig.Types.SerialConfig();

            EnabledToggle.IsOn = config.Enabled;
            EchoToggle.IsOn = config.Echo;
            BaudCombo.SelectedItem = config.Baud;
            TimeoutBox.Text = SettingsConfigUiUtil.UIntText(config.Timeout);
            ModeCombo.SelectedItem = config.Mode;
            RxdBox.Text = SettingsConfigUiUtil.UIntText(config.Rxd);
            TxdBox.Text = SettingsConfigUiUtil.UIntText(config.Txd);
            OverrideConsoleToggle.IsOn = config.OverrideConsoleSerialPort;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load serial configuration: " + ex.Message;
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

        if (BaudCombo.SelectedItem is not ModuleConfig.Types.SerialConfig.Types.Serial_Baud baud ||
            ModeCombo.SelectedItem is not ModuleConfig.Types.SerialConfig.Types.Serial_Mode mode)
        {
            StatusText.Text = "Select valid baud and mode values.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(TimeoutBox.Text, out var timeout) ||
            !SettingsConfigUiUtil.TryParseUInt(RxdBox.Text, out var rxd) ||
            !SettingsConfigUiUtil.TryParseUInt(TxdBox.Text, out var txd))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.SerialConfig
            {
                Enabled = EnabledToggle.IsOn,
                Echo = EchoToggle.IsOn,
                Baud = baud,
                Timeout = timeout,
                Mode = mode,
                Rxd = rxd,
                Txd = txd,
                OverrideConsoleSerialPort = OverrideConsoleToggle.IsOn
            };

            StatusText.Text = "Saving serial configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { Serial = config });
            StatusText.Text = "Serial configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Serial configuration saved. Reconnected."
                    : "Serial configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save serial configuration: " + ex.Message;
        }
    }
}
