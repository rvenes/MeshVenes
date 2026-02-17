using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsModuleRingtonePage : Page
{
    public SettingsModuleRingtonePage()
    {
        InitializeComponent();
        Loaded += SettingsModuleRingtonePage_Loaded;
    }

    private async void SettingsModuleRingtonePage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit ringtone settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading ringtone...";
            RingtoneBox.Text = await AdminConfigClient.Instance.GetRingtoneAsync(nodeNum);
            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load ringtone: " + ex.Message;
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

        try
        {
            StatusText.Text = "Saving ringtone...";
            await AdminConfigClient.Instance.SaveRingtoneAsync(nodeNum, RingtoneBox.Text ?? string.Empty);
            StatusText.Text = "Ringtone saved.";
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "Ringtone saved. Reconnected."
                    : "Ringtone may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save ringtone: " + ex.Message;
        }
    }
}
