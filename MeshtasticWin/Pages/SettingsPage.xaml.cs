using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsPage : Page
{
    private const string LastSettingsSectionKey = "LastSettingsSection";

    private string _currentSectionTag = "lora";

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (SettingsContentFrame.Content is null)
        {
            var last = Services.SettingsStore.GetString(LastSettingsSectionKey);
            _currentSectionTag = string.IsNullOrWhiteSpace(last) ? "lora" : last;
            NavigateToSection(_currentSectionTag);
        }
    }

    private void SectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
            return;

        NavigateToSection(tag);
    }

    private void SectionHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
            return;

        switch (tag)
        {
            case "radio-header":
                ToggleSection(RadioSectionPanel, RadioHeaderButton, "Radio Configuration");
                break;
            case "device-header":
                ToggleSection(DeviceSectionPanel, DeviceHeaderButton, "Device Configuration");
                break;
            case "module-header":
                ToggleSection(ModuleSectionPanel, ModuleHeaderButton, "Module Configuration");
                break;
            case "logging-header":
                ToggleSection(LoggingSectionPanel, LoggingHeaderButton, "Logging");
                break;
            case "firmware-header":
                ToggleSection(FirmwareSectionPanel, FirmwareHeaderButton, "Firmware");
                break;
        }
    }

    private static void ToggleSection(FrameworkElement panel, Button headerButton, string title)
    {
        var willExpand = panel.Visibility != Visibility.Visible;
        panel.Visibility = willExpand ? Visibility.Visible : Visibility.Collapsed;
        headerButton.Content = $"{(willExpand ? "▼" : "►")} {title}";
    }

    private void NavigateToSection(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            tag = "lora";

        _currentSectionTag = tag;
        Services.SettingsStore.SetString(LastSettingsSectionKey, tag);

        switch (tag)
        {
            case "lora":
                SettingsContentFrame.Navigate(typeof(SettingsRadioLoRaPage));
                break;
            case "channels":
                SettingsContentFrame.Navigate(typeof(SettingsRadioChannelsPage));
                break;
            case "security":
                SettingsContentFrame.Navigate(typeof(SettingsRadioSecurityPage));
                break;
            case "shareqr":
                SettingsContentFrame.Navigate(typeof(SettingsRadioShareQrPage));
                break;
            case "logging":
                SettingsContentFrame.Navigate(typeof(SettingsLoggingPage));
                break;
            case "logging-app":
                SettingsContentFrame.Navigate(typeof(SettingsLoggingPage));
                break;
            case "firmware":
                SettingsContentFrame.Navigate(typeof(SettingsFirmwarePage));
                break;
            case "firmware-remote-admin":
                SettingsContentFrame.Navigate(typeof(SettingsRemoteAdminPage));
                break;
            case "firmware-import-export":
                SettingsContentFrame.Navigate(typeof(SettingsImportExportPage));
                break;
            case "device-user":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceUserPage));
                break;
            case "device-bluetooth":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceBluetoothPage));
                break;
            case "device-device":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceDevicePage));
                break;
            case "device-display":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceDisplayPage));
                break;
            case "device-network":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceNetworkPage));
                break;
            case "device-position":
                SettingsContentFrame.Navigate(typeof(SettingsDevicePositionPage));
                break;
            case "device-power":
                SettingsContentFrame.Navigate(typeof(SettingsDevicePowerPage));
                break;
            case "module-canned":
                SettingsContentFrame.Navigate(typeof(SettingsModuleCannedMessagesPage));
                break;
            case "module-detection":
                SettingsContentFrame.Navigate(typeof(SettingsModuleDetectionSensorPage));
                break;
            case "module-external-notification":
                SettingsContentFrame.Navigate(typeof(SettingsModuleExternalNotificationPage));
                break;
            case "module-mqtt":
                SettingsContentFrame.Navigate(typeof(SettingsModuleMqttPage));
                break;
            case "module-range-test":
                SettingsContentFrame.Navigate(typeof(SettingsModuleRangeTestPage));
                break;
            case "module-pax":
                SettingsContentFrame.Navigate(typeof(SettingsModulePaxCounterPage));
                break;
            case "module-ringtone":
                SettingsContentFrame.Navigate(typeof(SettingsModuleRingtonePage));
                break;
            case "module-serial":
                SettingsContentFrame.Navigate(typeof(SettingsModuleSerialPage));
                break;
            case "module-store-forward":
                SettingsContentFrame.Navigate(typeof(SettingsModuleStoreForwardPage));
                break;
            case "module-telemetry":
                SettingsContentFrame.Navigate(typeof(SettingsModuleTelemetryPage));
                break;
            default:
                SettingsContentFrame.Navigate(typeof(SettingsComingSoonPage), tag);
                break;
        }
    }
}
