using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsRadioLoRaPage : Page
{
    private Config.Types.LoRaConfig? _current;

    public SettingsRadioLoRaPage()
    {
        InitializeComponent();
        Loaded += SettingsRadioLoRaPage_Loaded;
        PopulateEnums();
    }

    private async void SettingsRadioLoRaPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private void PopulateEnums()
    {
        ModemPresetCombo.ItemsSource = Enum.GetValues(typeof(Config.Types.LoRaConfig.Types.ModemPreset))
            .Cast<Config.Types.LoRaConfig.Types.ModemPreset>()
            .Where(v => (int)v >= 0)
            .ToList();

        RegionCombo.ItemsSource = Enum.GetValues(typeof(Config.Types.LoRaConfig.Types.RegionCode))
            .Cast<Config.Types.LoRaConfig.Types.RegionCode>()
            .Where(v => (int)v >= 0)
            .ToList();
    }

    private async Task LoadAsync()
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit LoRa settings.";
            SetEnabled(false);
            return;
        }

        try
        {
            SetEnabled(false);
            StatusText.Text = "Loading LoRa configuration...";

            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.LoraConfig);
            _current = config.Lora?.Clone() ?? new Config.Types.LoRaConfig();
            ApplyToForm(_current);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load LoRa configuration: " + ex.Message;
        }
        finally
        {
            SetEnabled(true);
        }
    }

    private void ApplyToForm(Config.Types.LoRaConfig value)
    {
        UsePresetToggle.IsOn = value.UsePreset;
        ModemPresetCombo.SelectedItem = value.ModemPreset;
        RegionCombo.SelectedItem = value.Region;

        BandwidthBox.Text = value.Bandwidth.ToString(CultureInfo.InvariantCulture);
        SpreadFactorBox.Text = value.SpreadFactor.ToString(CultureInfo.InvariantCulture);
        CodingRateBox.Text = value.CodingRate.ToString(CultureInfo.InvariantCulture);
        FrequencyOffsetBox.Text = value.FrequencyOffset.ToString(CultureInfo.InvariantCulture);

        HopLimitBox.Text = value.HopLimit.ToString(CultureInfo.InvariantCulture);
        TxEnabledToggle.IsOn = value.TxEnabled;
        TxPowerBox.Text = value.TxPower.ToString(CultureInfo.InvariantCulture);
        ChannelNumBox.Text = value.ChannelNum.ToString(CultureInfo.InvariantCulture);

        OverrideDutyCycleToggle.IsOn = value.OverrideDutyCycle;
        RxBoostedGainToggle.IsOn = value.Sx126XRxBoostedGain;
        OverrideFrequencyBox.Text = value.OverrideFrequency.ToString(CultureInfo.InvariantCulture);

        PaFanDisabledToggle.IsOn = value.PaFanDisabled;
        IgnoreMqttToggle.IsOn = value.IgnoreMqtt;
        ConfigOkToMqttToggle.IsOn = value.ConfigOkToMqtt;
    }

    private Config.Types.LoRaConfig? BuildFromForm(out string? error)
    {
        error = null;

        if (!TryParseUInt(BandwidthBox.Text, "Bandwidth", out var bandwidth, out error)) return null;
        if (!TryParseUInt(SpreadFactorBox.Text, "Spread factor", out var sf, out error)) return null;
        if (!TryParseUInt(CodingRateBox.Text, "Coding rate", out var cr, out error)) return null;
        if (!TryParseFloat(FrequencyOffsetBox.Text, "Frequency offset", out var freqOffset, out error)) return null;

        if (!TryParseUInt(HopLimitBox.Text, "Hop limit", out var hopLimit, out error)) return null;
        if (!TryParseInt(TxPowerBox.Text, "TX power", out var txPower, out error)) return null;
        if (!TryParseUInt(ChannelNumBox.Text, "Channel number", out var channelNum, out error)) return null;
        if (!TryParseFloat(OverrideFrequencyBox.Text, "Override frequency", out var overrideFrequency, out error)) return null;

        var modemPreset = ModemPresetCombo.SelectedItem is Config.Types.LoRaConfig.Types.ModemPreset preset
            ? preset
            : Config.Types.LoRaConfig.Types.ModemPreset.LongFast;

        var region = RegionCombo.SelectedItem is Config.Types.LoRaConfig.Types.RegionCode regionValue
            ? regionValue
            : Config.Types.LoRaConfig.Types.RegionCode.Unset;

        return new Config.Types.LoRaConfig
        {
            UsePreset = UsePresetToggle.IsOn,
            ModemPreset = modemPreset,
            Region = region,
            Bandwidth = bandwidth,
            SpreadFactor = sf,
            CodingRate = cr,
            FrequencyOffset = freqOffset,
            HopLimit = hopLimit,
            TxEnabled = TxEnabledToggle.IsOn,
            TxPower = txPower,
            ChannelNum = channelNum,
            OverrideDutyCycle = OverrideDutyCycleToggle.IsOn,
            Sx126XRxBoostedGain = RxBoostedGainToggle.IsOn,
            OverrideFrequency = overrideFrequency,
            PaFanDisabled = PaFanDisabledToggle.IsOn,
            IgnoreMqtt = IgnoreMqttToggle.IsOn,
            ConfigOkToMqtt = ConfigOkToMqttToggle.IsOn
        };
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentAsync();
    }

    private async Task SaveCurrentAsync()
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before saving.";
            return;
        }

        var lora = BuildFromForm(out var error);
        if (lora is null)
        {
            StatusText.Text = error ?? "Invalid value.";
            return;
        }

        try
        {
            SetEnabled(false);
            StatusText.Text = "Saving LoRa configuration...";

            var config = new Config { Lora = lora };
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, config);
            _current = lora.Clone();

            StatusText.Text = "LoRa configuration saved.";
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
                    ? "LoRa configuration saved. Reconnected."
                    : "LoRa configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save LoRa configuration: " + ex.Message;
        }
        finally
        {
            SetEnabled(true);
        }
    }

    private void SetEnabled(bool enabled)
    {
        UsePresetToggle.IsEnabled = enabled;
        ModemPresetCombo.IsEnabled = enabled;
        RegionCombo.IsEnabled = enabled;
        BandwidthBox.IsEnabled = enabled;
        SpreadFactorBox.IsEnabled = enabled;
        CodingRateBox.IsEnabled = enabled;
        FrequencyOffsetBox.IsEnabled = enabled;
        HopLimitBox.IsEnabled = enabled;
        TxEnabledToggle.IsEnabled = enabled;
        TxPowerBox.IsEnabled = enabled;
        ChannelNumBox.IsEnabled = enabled;
        OverrideDutyCycleToggle.IsEnabled = enabled;
        RxBoostedGainToggle.IsEnabled = enabled;
        OverrideFrequencyBox.IsEnabled = enabled;
        PaFanDisabledToggle.IsEnabled = enabled;
        IgnoreMqttToggle.IsEnabled = enabled;
        ConfigOkToMqttToggle.IsEnabled = enabled;
    }

    private static bool TryParseUInt(string text, string field, out uint value, out string? error)
    {
        if (!uint.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = field + " must be an unsigned number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseInt(string text, string field, out int value, out string? error)
    {
        if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = field + " must be a number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseFloat(string text, string field, out float value, out string? error)
    {
        if (!float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = field + " must be a decimal number (use '.' as separator).";
            return false;
        }

        error = null;
        return true;
    }
}
