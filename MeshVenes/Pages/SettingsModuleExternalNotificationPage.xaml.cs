using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleExternalNotificationPage : Page
{
    public SettingsModuleExternalNotificationPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleExternalNotificationPage_Loaded;
    }

    private async void SettingsModuleExternalNotificationPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit external notification settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading external notification configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.ExtnotifConfig);
            var config = module.ExternalNotification ?? new ModuleConfig.Types.ExternalNotificationConfig();

            EnabledToggle.IsOn = config.Enabled;
            AlertBellToggle.IsOn = config.AlertBell;
            AlertMessageToggle.IsOn = config.AlertMessage;
            UsePwmToggle.IsOn = config.UsePwm;
            UseI2sToggle.IsOn = config.UseI2SAsBuzzer;
            ActiveToggle.IsOn = config.Active;
            OutputPinBox.Text = SettingsConfigUiUtil.UIntText(config.Output);
            OutputMsBox.Text = SettingsConfigUiUtil.UIntText(config.OutputMs);
            NagTimeoutBox.Text = SettingsConfigUiUtil.UIntText(config.NagTimeout);
            AlertBellBuzzerToggle.IsOn = config.AlertBellBuzzer;
            AlertBellVibraToggle.IsOn = config.AlertBellVibra;
            AlertMessageBuzzerToggle.IsOn = config.AlertMessageBuzzer;
            AlertMessageVibraToggle.IsOn = config.AlertMessageVibra;
            OutputBuzzerBox.Text = SettingsConfigUiUtil.UIntText(config.OutputBuzzer);
            OutputVibraBox.Text = SettingsConfigUiUtil.UIntText(config.OutputVibra);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load external notification configuration: " + ex.Message;
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

        if (!SettingsConfigUiUtil.TryParseUInt(OutputPinBox.Text, out var outputPin) ||
            !SettingsConfigUiUtil.TryParseUInt(OutputMsBox.Text, out var outputMs) ||
            !SettingsConfigUiUtil.TryParseUInt(NagTimeoutBox.Text, out var nagTimeout) ||
            !SettingsConfigUiUtil.TryParseUInt(OutputBuzzerBox.Text, out var outputBuzzer) ||
            !SettingsConfigUiUtil.TryParseUInt(OutputVibraBox.Text, out var outputVibra))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.ExternalNotificationConfig
            {
                Enabled = EnabledToggle.IsOn,
                AlertBell = AlertBellToggle.IsOn,
                AlertMessage = AlertMessageToggle.IsOn,
                UsePwm = UsePwmToggle.IsOn,
                UseI2SAsBuzzer = UseI2sToggle.IsOn,
                Active = ActiveToggle.IsOn,
                Output = outputPin,
                OutputMs = outputMs,
                NagTimeout = nagTimeout,
                AlertBellBuzzer = AlertBellBuzzerToggle.IsOn,
                AlertBellVibra = AlertBellVibraToggle.IsOn,
                AlertMessageBuzzer = AlertMessageBuzzerToggle.IsOn,
                AlertMessageVibra = AlertMessageVibraToggle.IsOn,
                OutputBuzzer = outputBuzzer,
                OutputVibra = outputVibra
            };

            StatusText.Text = "Saving external notification configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { ExternalNotification = config });
            StatusText.Text = "External notification configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "External notification configuration saved. Reconnected."
                    : "External notification configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save external notification configuration: " + ex.Message;
        }
    }
}
