using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsRadioSecurityPage : Page
{
    public SettingsRadioSecurityPage()
    {
        InitializeComponent();
        Loaded += SettingsRadioSecurityPage_Loaded;
    }

    private async void SettingsRadioSecurityPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit security settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading security configuration...";
            var config = await AdminConfigClient.Instance.GetConfigAsync(nodeNum, AdminMessage.Types.ConfigType.SecurityConfig);
            var security = config.Security ?? new Config.Types.SecurityConfig();

            PublicKeyBox.Text = security.PublicKey?.ToBase64() ?? "";
            PrivateKeyBox.Text = security.PrivateKey?.ToBase64() ?? "";

            var adminKeys = security.AdminKey?.Select(k => k.ToBase64()).ToList() ?? new List<string>();
            AdminKey1Box.Text = adminKeys.ElementAtOrDefault(0) ?? "";
            AdminKey2Box.Text = adminKeys.ElementAtOrDefault(1) ?? "";
            AdminKey3Box.Text = adminKeys.ElementAtOrDefault(2) ?? "";

            IsManagedToggle.IsOn = security.IsManaged;
            SerialEnabledToggle.IsOn = security.SerialEnabled;
            DebugLogApiToggle.IsOn = security.DebugLogApiEnabled;
            AdminChannelToggle.IsOn = security.AdminChannelEnabled;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load security configuration: " + ex.Message;
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
            var security = new Config.Types.SecurityConfig
            {
                PublicKey = ParseBase64(PublicKeyBox.Text),
                PrivateKey = ParseBase64(PrivateKeyBox.Text),
                IsManaged = IsManagedToggle.IsOn,
                SerialEnabled = SerialEnabledToggle.IsOn,
                DebugLogApiEnabled = DebugLogApiToggle.IsOn,
                AdminChannelEnabled = AdminChannelToggle.IsOn
            };

            foreach (var key in new[] { AdminKey1Box.Text, AdminKey2Box.Text, AdminKey3Box.Text })
            {
                var parsed = ParseBase64(key, allowEmpty: true);
                if (parsed.Length > 0)
                    security.AdminKey.Add(parsed);
            }

            StatusText.Text = "Saving security configuration...";
            var config = new Config { Security = security };
            await AdminConfigClient.Instance.SaveConfigAsync(nodeNum, config);
            StatusText.Text = "Security configuration saved.";
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
                    ? "Security configuration saved. Reconnected."
                    : "Security configuration may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save security configuration: " + ex.Message;
        }
    }

    private static ByteString ParseBase64(string? text, bool allowEmpty = true)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return allowEmpty ? ByteString.Empty : throw new InvalidOperationException("Key cannot be empty.");

        return ByteString.FromBase64(value);
    }
}
