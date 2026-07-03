using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeshVenes.Pages;

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
        else
        {
            ExpandSectionForTag(_currentSectionTag);
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
            case "appearance-header":
                ToggleSection(AppearanceSectionPanel, AppearanceHeaderButton, "Appearance");
                break;
        }
    }

    private static void ToggleSection(FrameworkElement panel, Button headerButton, string title)
    {
        var willExpand = panel.Visibility != Visibility.Visible;
        SetSectionExpanded(panel, headerButton, title, willExpand);
    }

    private static void SetSectionExpanded(FrameworkElement panel, Button headerButton, string title, bool isExpanded)
    {
        panel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        headerButton.Content = $"{(isExpanded ? "▼" : "►")} {title}";
    }

    private void CollapseAllSections()
    {
        SetSectionExpanded(RadioSectionPanel, RadioHeaderButton, "Radio Configuration", false);
        SetSectionExpanded(DeviceSectionPanel, DeviceHeaderButton, "Device Configuration", false);
        SetSectionExpanded(ModuleSectionPanel, ModuleHeaderButton, "Module Configuration", false);
        SetSectionExpanded(LoggingSectionPanel, LoggingHeaderButton, "Logging", false);
        SetSectionExpanded(FirmwareSectionPanel, FirmwareHeaderButton, "Firmware", false);
        SetSectionExpanded(AppearanceSectionPanel, AppearanceHeaderButton, "Appearance", false);
    }

    private void ExpandSectionForTag(string tag)
    {
        CollapseAllSections();
        switch (tag)
        {
            case "lora":
            case "channels":
            case "security":
            case "shareqr":
                SetSectionExpanded(RadioSectionPanel, RadioHeaderButton, "Radio Configuration", true);
                break;
            case "device-user":
            case "device-bluetooth":
            case "device-device":
            case "device-display":
            case "device-network":
            case "device-position":
            case "device-power":
                SetSectionExpanded(DeviceSectionPanel, DeviceHeaderButton, "Device Configuration", true);
                break;
            case "module-canned":
            case "module-detection":
            case "module-external-notification":
            case "module-mqtt":
            case "module-range-test":
            case "module-pax":
            case "module-ringtone":
            case "module-serial":
            case "module-store-forward":
            case "module-telemetry":
                SetSectionExpanded(ModuleSectionPanel, ModuleHeaderButton, "Module Configuration", true);
                break;
            case "logging":
            case "logging-app":
                SetSectionExpanded(LoggingSectionPanel, LoggingHeaderButton, "Logging", true);
                break;
            case "firmware":
            case "firmware-remote-admin":
            case "firmware-import-export":
                SetSectionExpanded(FirmwareSectionPanel, FirmwareHeaderButton, "Firmware", true);
                break;
            case "appearance-theme":
                SetSectionExpanded(AppearanceSectionPanel, AppearanceHeaderButton, "Appearance", true);
                break;
            default:
                break;
        }
    }

    private void NavigateToSection(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            tag = "lora";

        _currentSectionTag = tag;
        Services.SettingsStore.SetString(LastSettingsSectionKey, tag);
        ExpandSectionForTag(tag);

        switch (tag)
        {
            case "lora":
                SettingsContentFrame.Navigate(typeof(SettingsRadioLoRaPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "channels":
                SettingsContentFrame.Navigate(typeof(SettingsRadioChannelsPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "security":
                SettingsContentFrame.Navigate(typeof(SettingsRadioSecurityPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "shareqr":
                SettingsContentFrame.Navigate(typeof(SettingsRadioShareQrPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "logging":
                SettingsContentFrame.Navigate(typeof(SettingsLoggingPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "logging-app":
                SettingsContentFrame.Navigate(typeof(SettingsLoggingPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "firmware":
                SettingsContentFrame.Navigate(typeof(SettingsFirmwarePage), null, new SuppressNavigationTransitionInfo());
                break;
            case "firmware-remote-admin":
                SettingsContentFrame.Navigate(typeof(SettingsRemoteAdminPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "firmware-import-export":
                SettingsContentFrame.Navigate(typeof(SettingsImportExportPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-user":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceUserPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-bluetooth":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceBluetoothPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-device":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceDevicePage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-display":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceDisplayPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-network":
                SettingsContentFrame.Navigate(typeof(SettingsDeviceNetworkPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-position":
                SettingsContentFrame.Navigate(typeof(SettingsDevicePositionPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "device-power":
                SettingsContentFrame.Navigate(typeof(SettingsDevicePowerPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-canned":
                SettingsContentFrame.Navigate(typeof(SettingsModuleCannedMessagesPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-detection":
                SettingsContentFrame.Navigate(typeof(SettingsModuleDetectionSensorPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-external-notification":
                SettingsContentFrame.Navigate(typeof(SettingsModuleExternalNotificationPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-mqtt":
                SettingsContentFrame.Navigate(typeof(SettingsModuleMqttPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-range-test":
                SettingsContentFrame.Navigate(typeof(SettingsModuleRangeTestPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-pax":
                SettingsContentFrame.Navigate(typeof(SettingsModulePaxCounterPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-ringtone":
                SettingsContentFrame.Navigate(typeof(SettingsModuleRingtonePage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-serial":
                SettingsContentFrame.Navigate(typeof(SettingsModuleSerialPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-store-forward":
                SettingsContentFrame.Navigate(typeof(SettingsModuleStoreForwardPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "module-telemetry":
                SettingsContentFrame.Navigate(typeof(SettingsModuleTelemetryPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "appearance-theme":
                SettingsContentFrame.Navigate(typeof(SettingsAppearancePage), null, new SuppressNavigationTransitionInfo());
                break;
            default:
                SettingsContentFrame.Navigate(typeof(SettingsComingSoonPage), tag, new SuppressNavigationTransitionInfo());
                break;
        }
    }
}
