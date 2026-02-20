using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleRangeTestPage : Page
{
    public SettingsModuleRangeTestPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleRangeTestPage_Loaded;
    }

    private async void SettingsModuleRangeTestPage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit range test settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading range test configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.RangetestConfig);
            var config = module.RangeTest ?? new ModuleConfig.Types.RangeTestConfig();

            EnabledToggle.IsOn = config.Enabled;
            SenderBox.Text = SettingsConfigUiUtil.UIntText(config.Sender);
            SaveToggle.IsOn = config.Save;
            ClearOnRebootToggle.IsOn = config.ClearOnReboot;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load range test configuration: " + ex.Message;
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

        if (!SettingsConfigUiUtil.TryParseUInt(SenderBox.Text, out var senderInterval))
        {
            StatusText.Text = "Sender interval must be an unsigned number.";
            return;
        }

        try
        {
            var config = new ModuleConfig.Types.RangeTestConfig
            {
                Enabled = EnabledToggle.IsOn,
                Sender = senderInterval,
                Save = SaveToggle.IsOn,
                ClearOnReboot = ClearOnRebootToggle.IsOn
            };

            StatusText.Text = "Saving range test configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { RangeTest = config });
            StatusText.Text = "Range test configuration saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Range test configuration saved. Reconnected."
                    : "Range test configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save range test configuration: " + ex.Message;
        }
    }
}
