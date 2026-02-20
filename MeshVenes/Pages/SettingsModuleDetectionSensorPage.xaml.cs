using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleDetectionSensorPage : Page
{
    public SettingsModuleDetectionSensorPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleDetectionSensorPage_Loaded;
        TriggerCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<ModuleConfig.Types.DetectionSensorConfig.Types.TriggerType>();
    }

    private async void SettingsModuleDetectionSensorPage_Loaded(object sender, RoutedEventArgs e)
    {
        ShowDetectionSensorLogTabToggle.IsOn = AppState.ShowDetectionSensorLogTab;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit detection sensor settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading detection sensor configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.DetectionsensorConfig);
            var config = module.DetectionSensor ?? new ModuleConfig.Types.DetectionSensorConfig();

            EnabledToggle.IsOn = config.Enabled;
            SendBellToggle.IsOn = config.SendBell;
            NameBox.Text = config.Name ?? string.Empty;
            MonitorPinBox.Text = SettingsConfigUiUtil.UIntText(config.MonitorPin);
            TriggerCombo.SelectedItem = config.DetectionTriggerType;
            MinimumBroadcastBox.Text = SettingsConfigUiUtil.UIntText(config.MinimumBroadcastSecs);
            StateBroadcastBox.Text = SettingsConfigUiUtil.UIntText(config.StateBroadcastSecs);
            PullupToggle.IsOn = config.UsePullup;
            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load detection sensor configuration: " + ex.Message;
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

        if (TriggerCombo.SelectedItem is not ModuleConfig.Types.DetectionSensorConfig.Types.TriggerType trigger)
        {
            StatusText.Text = "Select a trigger type.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(MonitorPinBox.Text, out var monitorPin) ||
            !SettingsConfigUiUtil.TryParseUInt(MinimumBroadcastBox.Text, out var minimumBroadcast) ||
            !SettingsConfigUiUtil.TryParseUInt(StateBroadcastBox.Text, out var stateBroadcast))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.DetectionSensorConfig
            {
                Enabled = EnabledToggle.IsOn,
                SendBell = SendBellToggle.IsOn,
                Name = (NameBox.Text ?? string.Empty).Trim(),
                MonitorPin = monitorPin,
                DetectionTriggerType = trigger,
                MinimumBroadcastSecs = minimumBroadcast,
                StateBroadcastSecs = stateBroadcast,
                UsePullup = PullupToggle.IsOn
            };

            StatusText.Text = "Saving detection sensor configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { DetectionSensor = config });
            StatusText.Text = "Detection sensor configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Detection sensor configuration saved. Reconnected."
                    : "Detection sensor configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save detection sensor configuration: " + ex.Message;
        }
    }

    private void ShowDetectionSensorLogTabToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppState.ShowDetectionSensorLogTab = ShowDetectionSensorLogTabToggle.IsOn;
    }
}
