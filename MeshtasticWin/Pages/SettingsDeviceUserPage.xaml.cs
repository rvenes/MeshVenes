using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsDeviceUserPage : Page
{
    public SettingsDeviceUserPage()
    {
        InitializeComponent();
        Loaded += SettingsDeviceUserPage_Loaded;
    }

    private async void SettingsDeviceUserPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit user settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading user configuration...";
            var owner = await AdminConfigClient.Instance.GetOwnerAsync(nodeNum);
            LongNameBox.Text = owner.LongName ?? string.Empty;
            ShortNameBox.Text = owner.ShortName ?? string.Empty;
            UnmessagableToggle.IsOn = owner.HasIsUnmessagable && owner.IsUnmessagable;
            LicensedToggle.IsOn = owner.IsLicensed;
            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load user configuration: " + ex.Message;
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

        try
        {
            var owner = new User
            {
                LongName = (LongNameBox.Text ?? string.Empty).Trim(),
                ShortName = (ShortNameBox.Text ?? string.Empty).Trim(),
                IsLicensed = LicensedToggle.IsOn,
                IsUnmessagable = UnmessagableToggle.IsOn
            };

            StatusText.Text = "Saving user configuration...";
            await AdminConfigClient.Instance.SaveOwnerAsync(nodeNum, owner);
            StatusText.Text = "User configuration saved.";
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
                    ? "User configuration saved. Reconnected."
                    : "User configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save user configuration: " + ex.Message;
        }
    }
}
