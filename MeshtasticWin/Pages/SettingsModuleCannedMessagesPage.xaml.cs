using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsModuleCannedMessagesPage : Page
{
    public SettingsModuleCannedMessagesPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleCannedMessagesPage_Loaded;
        var events = SettingsConfigUiUtil.EnumValues<ModuleConfig.Types.CannedMessageConfig.Types.InputEventChar>();
        CwEventCombo.ItemsSource = events;
        CcwEventCombo.ItemsSource = events;
        PressEventCombo.ItemsSource = events;
    }

    private async void SettingsModuleCannedMessagesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit canned message settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading canned messages configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.CannedmsgConfig);
            var canned = module.CannedMessage ?? new ModuleConfig.Types.CannedMessageConfig();
            var messages = await AdminConfigClient.Instance.GetCannedMessageModuleMessagesAsync(nodeNum);

            EnabledToggle.IsOn = canned.Enabled;
            SendBellToggle.IsOn = canned.SendBell;
            MessagesBox.Text = messages;
            RotaryToggle.IsOn = canned.Rotary1Enabled;
            UpDownToggle.IsOn = canned.Updown1Enabled;
            PinABox.Text = SettingsConfigUiUtil.UIntText(canned.InputbrokerPinA);
            PinBBox.Text = SettingsConfigUiUtil.UIntText(canned.InputbrokerPinB);
            PinPressBox.Text = SettingsConfigUiUtil.UIntText(canned.InputbrokerPinPress);
            CwEventCombo.SelectedItem = canned.InputbrokerEventCw;
            CcwEventCombo.SelectedItem = canned.InputbrokerEventCcw;
            PressEventCombo.SelectedItem = canned.InputbrokerEventPress;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load canned messages configuration: " + ex.Message;
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

        if (CwEventCombo.SelectedItem is not ModuleConfig.Types.CannedMessageConfig.Types.InputEventChar cw ||
            CcwEventCombo.SelectedItem is not ModuleConfig.Types.CannedMessageConfig.Types.InputEventChar ccw ||
            PressEventCombo.SelectedItem is not ModuleConfig.Types.CannedMessageConfig.Types.InputEventChar press)
        {
            StatusText.Text = "Select valid key mapping events.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(PinABox.Text, out var pinA) ||
            !SettingsConfigUiUtil.TryParseUInt(PinBBox.Text, out var pinB) ||
            !SettingsConfigUiUtil.TryParseUInt(PinPressBox.Text, out var pinPress))
        {
            StatusText.Text = "Pin values must be unsigned numbers.";
            return;
        }

        try
        {
            var canned = new ModuleConfig.Types.CannedMessageConfig
            {
                Enabled = EnabledToggle.IsOn,
                SendBell = SendBellToggle.IsOn,
                Rotary1Enabled = RotaryToggle.IsOn,
                Updown1Enabled = UpDownToggle.IsOn,
                InputbrokerPinA = pinA,
                InputbrokerPinB = pinB,
                InputbrokerPinPress = pinPress,
                InputbrokerEventCw = cw,
                InputbrokerEventCcw = ccw,
                InputbrokerEventPress = press
            };

            StatusText.Text = "Saving canned messages configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { CannedMessage = canned });
            await AdminConfigClient.Instance.SaveCannedMessageModuleMessagesAsync(nodeNum, (MessagesBox.Text ?? string.Empty).Trim());
            StatusText.Text = "Canned messages configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Canned messages configuration saved. Reconnected."
                    : "Canned messages configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save canned messages configuration: " + ex.Message;
        }
    }
}
