using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsDevicePowerPage : Page
{
    public SettingsDevicePowerPage()
    {
        InitializeComponent();
        Loaded += SettingsDevicePowerPage_Loaded;
    }

    private async void SettingsDevicePowerPage_Loaded(object sender, RoutedEventArgs e)
    {
        ShowPowerMetricsTabToggle.IsOn = AppState.ShowPowerMetricsTab;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit power settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading power configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PowerConfig);
            var power = config.Power ?? new Config.Types.PowerConfig();

            PowerSavingToggle.IsOn = power.IsPowerSaving;
            ShutdownOnPowerLossToggle.IsOn = power.OnBatteryShutdownAfterSecs > 0;
            ShutdownAfterSecsBox.Text = SettingsConfigUiUtil.UIntText(power.OnBatteryShutdownAfterSecs);
            AdcOverrideBox.Text = power.AdcMultiplierOverride.ToString(CultureInfo.InvariantCulture);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load power configuration: " + ex.Message;
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

        if (!SettingsConfigUiUtil.TryParseUInt(ShutdownAfterSecsBox.Text, out var shutdownAfterSecs))
        {
            StatusText.Text = "Shutdown delay must be an unsigned number.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseFloat(AdcOverrideBox.Text, out var adcOverride))
        {
            StatusText.Text = "ADC override must be a decimal number.";
            return;
        }

        if (!ShutdownOnPowerLossToggle.IsOn)
            shutdownAfterSecs = 0;
        else if (shutdownAfterSecs == 0)
        {
            StatusText.Text = "Set shutdown delay above 0 when shutdown on power loss is enabled.";
            return;
        }

        try
        {
            var power = new Config.Types.PowerConfig
            {
                IsPowerSaving = PowerSavingToggle.IsOn,
                OnBatteryShutdownAfterSecs = shutdownAfterSecs,
                AdcMultiplierOverride = adcOverride
            };

            StatusText.Text = "Saving power configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Power = power });
            StatusText.Text = "Power configuration saved.";
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
                    ? "Power configuration saved. Reconnected."
                    : "Power configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save power configuration: " + ex.Message;
        }
    }

    private void ShowPowerMetricsTabToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppState.ShowPowerMetricsTab = ShowPowerMetricsTabToggle.IsOn;
    }
}
