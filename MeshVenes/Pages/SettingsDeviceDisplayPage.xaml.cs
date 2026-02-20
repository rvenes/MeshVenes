using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsDeviceDisplayPage : Page
{
    public SettingsDeviceDisplayPage()
    {
        InitializeComponent();
        Loaded += SettingsDeviceDisplayPage_Loaded;
        UnitsCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.DisplayConfig.Types.DisplayUnits>();
        DisplayModeCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.DisplayConfig.Types.DisplayMode>();
        OledTypeCombo.ItemsSource = SettingsConfigUiUtil.EnumValues<Config.Types.DisplayConfig.Types.OledType>();
    }

    private async void SettingsDeviceDisplayPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit display settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading display configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.DisplayConfig);
            var display = config.Display ?? new Config.Types.DisplayConfig();

            AlwaysNorthToggle.IsOn = display.CompassNorthTop;
            Clock12Toggle.IsOn = display.Use12HClock;
            BoldHeadingToggle.IsOn = display.HeadingBold;
            UnitsCombo.SelectedItem = display.Units;
            ScreenOnSecsBox.Text = SettingsConfigUiUtil.UIntText(display.ScreenOnSecs);
            CarouselSecsBox.Text = SettingsConfigUiUtil.UIntText(display.AutoScreenCarouselSecs);
            WakeOnMotionToggle.IsOn = display.WakeOnTapOrMotion;
            FlipScreenToggle.IsOn = display.FlipScreen;
            DisplayModeCombo.SelectedItem = display.Displaymode;
            OledTypeCombo.SelectedItem = display.Oled;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load display configuration: " + ex.Message;
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

        if (UnitsCombo.SelectedItem is not Config.Types.DisplayConfig.Types.DisplayUnits units ||
            DisplayModeCombo.SelectedItem is not Config.Types.DisplayConfig.Types.DisplayMode displayMode ||
            OledTypeCombo.SelectedItem is not Config.Types.DisplayConfig.Types.OledType oledType)
        {
            StatusText.Text = "Select valid enum values.";
            return;
        }

        if (!SettingsConfigUiUtil.TryParseUInt(ScreenOnSecsBox.Text, out var screenOnSecs) ||
            !SettingsConfigUiUtil.TryParseUInt(CarouselSecsBox.Text, out var carouselSecs))
        {
            StatusText.Text = "Numeric fields must contain unsigned numbers.";
            return;
        }

        try
        {
            var display = new Config.Types.DisplayConfig
            {
                CompassNorthTop = AlwaysNorthToggle.IsOn,
                Use12HClock = Clock12Toggle.IsOn,
                HeadingBold = BoldHeadingToggle.IsOn,
                Units = units,
                ScreenOnSecs = screenOnSecs,
                AutoScreenCarouselSecs = carouselSecs,
                WakeOnTapOrMotion = WakeOnMotionToggle.IsOn,
                FlipScreen = FlipScreenToggle.IsOn,
                Displaymode = displayMode,
                Oled = oledType
            };

            StatusText.Text = "Saving display configuration...";
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, new Config { Display = display });
            StatusText.Text = "Display configuration saved.";
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
                    ? "Display configuration saved. Reconnected."
                    : "Display configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save display configuration: " + ex.Message;
        }
    }
}
