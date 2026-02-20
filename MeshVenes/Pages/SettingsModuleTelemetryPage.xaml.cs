using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleTelemetryPage : Page
{
    public SettingsModuleTelemetryPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleTelemetryPage_Loaded;
    }

    private async void SettingsModuleTelemetryPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit telemetry settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading telemetry configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.TelemetryConfig);
            var config = module.Telemetry ?? new ModuleConfig.Types.TelemetryConfig();

            DeviceTelemetryToggle.IsOn = config.DeviceTelemetryEnabled;
            DeviceUpdateIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.DeviceUpdateInterval);
            EnvironmentEnabledToggle.IsOn = config.EnvironmentMeasurementEnabled;
            EnvironmentUpdateIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.EnvironmentUpdateInterval);
            EnvironmentScreenToggle.IsOn = config.EnvironmentScreenEnabled;
            FahrenheitToggle.IsOn = config.EnvironmentDisplayFahrenheit;
            AirQualityToggle.IsOn = config.AirQualityEnabled;
            AirQualityIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.AirQualityInterval);
            PowerEnabledToggle.IsOn = config.PowerMeasurementEnabled;
            PowerUpdateIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.PowerUpdateInterval);
            PowerScreenToggle.IsOn = config.PowerScreenEnabled;
            HealthEnabledToggle.IsOn = config.HealthMeasurementEnabled;
            HealthUpdateIntervalBox.Text = SettingsConfigUiUtil.UIntText(config.HealthUpdateInterval);
            HealthScreenToggle.IsOn = config.HealthScreenEnabled;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load telemetry configuration: " + ex.Message;
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

        if (!SettingsConfigUiUtil.TryParseUInt(DeviceUpdateIntervalBox.Text, out var deviceInterval) ||
            !SettingsConfigUiUtil.TryParseUInt(EnvironmentUpdateIntervalBox.Text, out var envInterval) ||
            !SettingsConfigUiUtil.TryParseUInt(AirQualityIntervalBox.Text, out var airInterval) ||
            !SettingsConfigUiUtil.TryParseUInt(PowerUpdateIntervalBox.Text, out var powerInterval) ||
            !SettingsConfigUiUtil.TryParseUInt(HealthUpdateIntervalBox.Text, out var healthInterval))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.TelemetryConfig
            {
                DeviceTelemetryEnabled = DeviceTelemetryToggle.IsOn,
                DeviceUpdateInterval = deviceInterval,
                EnvironmentMeasurementEnabled = EnvironmentEnabledToggle.IsOn,
                EnvironmentUpdateInterval = envInterval,
                EnvironmentScreenEnabled = EnvironmentScreenToggle.IsOn,
                EnvironmentDisplayFahrenheit = FahrenheitToggle.IsOn,
                AirQualityEnabled = AirQualityToggle.IsOn,
                AirQualityInterval = airInterval,
                PowerMeasurementEnabled = PowerEnabledToggle.IsOn,
                PowerUpdateInterval = powerInterval,
                PowerScreenEnabled = PowerScreenToggle.IsOn,
                HealthMeasurementEnabled = HealthEnabledToggle.IsOn,
                HealthUpdateInterval = healthInterval,
                HealthScreenEnabled = HealthScreenToggle.IsOn
            };

            StatusText.Text = "Saving telemetry configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { Telemetry = config });
            StatusText.Text = "Telemetry configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Telemetry configuration saved. Reconnected."
                    : "Telemetry configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save telemetry configuration: " + ex.Message;
        }
    }
}
