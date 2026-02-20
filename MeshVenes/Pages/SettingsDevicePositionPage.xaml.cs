using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsDevicePositionPage : Page
{
    public SettingsDevicePositionPage()
    {
        InitializeComponent();
        Loaded += SettingsDevicePositionPage_Loaded;
        GpsModeCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.PositionConfig.Types.GpsMode>();
    }

    private async void SettingsDevicePositionPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit position settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading position configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.PositionConfig);
            var position = config.Position ?? new Config.Types.PositionConfig();

            BroadcastIntervalBox.Text = SettingsConfigUiUtil.UIntText(position.PositionBroadcastSecs);
            SmartPositionToggle.IsOn = position.PositionBroadcastSmartEnabled;
            MinimumIntervalBox.Text = SettingsConfigUiUtil.UIntText(position.BroadcastSmartMinimumIntervalSecs);
            MinimumDistanceBox.Text = SettingsConfigUiUtil.UIntText(position.BroadcastSmartMinimumDistance);
            GpsModeCombo.SelectedItem = position.GpsMode;
            FixedPositionToggle.IsOn = position.FixedPosition;

            var flags = position.PositionFlags;
            FlagAltitudeToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Altitude);
            FlagSatellitesToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Satinview);
            FlagSequenceToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.SeqNo);
            FlagTimestampToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Timestamp);
            FlagHeadingToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Heading);
            FlagSpeedToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Speed);
            FlagAltitudeMslToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.AltitudeMsl);
            FlagGeoidalToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.GeoidalSeparation);
            FlagDopToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Dop);
            FlagHvDopToggle.IsOn = HasFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Hvdop);

            GpsRxBox.Text = SettingsConfigUiUtil.UIntText(position.RxGpio);
            GpsTxBox.Text = SettingsConfigUiUtil.UIntText(position.TxGpio);
            GpsEnBox.Text = SettingsConfigUiUtil.UIntText(position.GpsEnGpio);

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load position configuration: " + ex.Message;
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

        if (GpsModeCombo.SelectedItem is not Config.Types.PositionConfig.Types.GpsMode gpsMode)
        {
            StatusText.Text = "Select a GPS mode.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(BroadcastIntervalBox.Text, out var broadcastSecs) ||
            !SettingsConfigUiUtil.TryParseUInt(MinimumIntervalBox.Text, out var minimumIntervalSecs) ||
            !SettingsConfigUiUtil.TryParseUInt(MinimumDistanceBox.Text, out var minimumDistanceMeters) ||
            !SettingsConfigUiUtil.TryParseUInt(GpsRxBox.Text, out var gpsRx) ||
            !SettingsConfigUiUtil.TryParseUInt(GpsTxBox.Text, out var gpsTx) ||
            !SettingsConfigUiUtil.TryParseUInt(GpsEnBox.Text, out var gpsEn))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            uint flags = 0;
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Altitude, FlagAltitudeToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Satinview, FlagSatellitesToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.SeqNo, FlagSequenceToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Timestamp, FlagTimestampToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Heading, FlagHeadingToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Speed, FlagSpeedToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.AltitudeMsl, FlagAltitudeMslToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.GeoidalSeparation, FlagGeoidalToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Dop, FlagDopToggle.IsOn);
            flags = SetFlag(flags, Config.Types.PositionConfig.Types.PositionFlags.Hvdop, FlagHvDopToggle.IsOn);

            var position = new Config.Types.PositionConfig
            {
                PositionBroadcastSecs = broadcastSecs,
                PositionBroadcastSmartEnabled = SmartPositionToggle.IsOn,
                BroadcastSmartMinimumIntervalSecs = minimumIntervalSecs,
                BroadcastSmartMinimumDistance = minimumDistanceMeters,
                GpsMode = gpsMode,
                FixedPosition = FixedPositionToggle.IsOn,
                PositionFlags = flags,
                RxGpio = gpsRx,
                TxGpio = gpsTx,
                GpsEnGpio = gpsEn
            };

            StatusText.Text = "Saving position configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Position = position });
            StatusText.Text = "Position configuration saved.";
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
                    ? "Position configuration saved. Reconnected."
                    : "Position configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save position configuration: " + ex.Message;
        }
    }

    private static bool HasFlag(uint source, Config.Types.PositionConfig.Types.PositionFlags flag)
        => (source & (uint)flag) != 0;

    private static uint SetFlag(uint source, Config.Types.PositionConfig.Types.PositionFlags flag, bool enabled)
        => enabled ? source | (uint)flag : source & ~(uint)flag;
}
