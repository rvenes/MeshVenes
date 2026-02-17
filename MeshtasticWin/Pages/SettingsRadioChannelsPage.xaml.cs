using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshtasticWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsRadioChannelsPage : Page
{
    public ObservableCollection<ChannelRowVm> Rows { get; } = new();

    public SettingsRadioChannelsPage()
    {
        InitializeComponent();
        Loaded += SettingsRadioChannelsPage_Loaded;
    }

    private async void SettingsRadioChannelsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Rows.Clear();

        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit channels.";
            return;
        }

        try
        {
            StatusText.Text = "Loading channels...";
            var channels = await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, maxChannels: 8);
            foreach (var channel in channels.OrderBy(c => c.Index))
                Rows.Add(ChannelRowVm.FromChannel(channel));

            StatusText.Text = $"Loaded {Rows.Count} channels from node 0x{nodeNum:x8}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load channels: " + ex.Message;
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
            var channels = Rows
                .OrderBy(r => r.Index)
                .Select(r => r.ToChannel())
                .ToList();

            await AdminConfigClient.Instance.SaveChannelsAsync(nodeNum, channels);
            StatusText.Text = "Channels saved.";
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
                    ? "Channels saved. Reconnected."
                    : "Channels may be saved, but reconnect failed.";
                return;
            }

            StatusText.Text = "Failed to save channels: " + ex.Message;
        }
    }
}

public sealed class ChannelRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; set; }

    private int _roleIndex;
    public int RoleIndex
    {
        get => _roleIndex;
        set
        {
            if (_roleIndex == value) return;
            _roleIndex = Math.Clamp(value, 0, 2);
            OnChanged();
        }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value ?? ""; OnChanged(); }
    }

    private string _pskBase64 = "";
    public string PskBase64
    {
        get => _pskBase64;
        set { if (_pskBase64 == value) return; _pskBase64 = value ?? ""; OnChanged(); }
    }

    private string _channelIdText = "0";
    public string ChannelIdText
    {
        get => _channelIdText;
        set { if (_channelIdText == value) return; _channelIdText = value ?? ""; OnChanged(); }
    }

    private bool _uplinkEnabled;
    public bool UplinkEnabled
    {
        get => _uplinkEnabled;
        set { if (_uplinkEnabled == value) return; _uplinkEnabled = value; OnChanged(); }
    }

    private bool _downlinkEnabled;
    public bool DownlinkEnabled
    {
        get => _downlinkEnabled;
        set { if (_downlinkEnabled == value) return; _downlinkEnabled = value; OnChanged(); }
    }

    public static ChannelRowVm FromChannel(Channel channel)
    {
        var settings = channel.Settings;
        return new ChannelRowVm
        {
            Index = channel.Index,
            RoleIndex = channel.Role switch
            {
                Channel.Types.Role.Primary => 1,
                Channel.Types.Role.Secondary => 2,
                _ => 0
            },
            Name = settings?.Name ?? "",
            PskBase64 = settings?.Psk is { Length: > 0 } psk ? psk.ToBase64() : "",
            ChannelIdText = settings?.Id.ToString(CultureInfo.InvariantCulture) ?? "0",
            UplinkEnabled = settings?.UplinkEnabled ?? false,
            DownlinkEnabled = settings?.DownlinkEnabled ?? false
        };
    }

    public Channel ToChannel()
    {
        var role = RoleIndex switch
        {
            1 => Channel.Types.Role.Primary,
            2 => Channel.Types.Role.Secondary,
            _ => Channel.Types.Role.Disabled
        };

        var channel = new Channel
        {
            Index = Index,
            Role = role
        };

        if (role == Channel.Types.Role.Disabled)
            return channel;

        var settings = new ChannelSettings
        {
            Name = (Name ?? "").Trim(),
            UplinkEnabled = UplinkEnabled,
            DownlinkEnabled = DownlinkEnabled
        };

        var idText = (ChannelIdText ?? "").Trim();
        if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            idText = idText.Substring(2);

        if (uint.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idDec))
            settings.Id = idDec;
        else if (uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var idHex))
            settings.Id = idHex;

        var pskText = (PskBase64 ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(pskText))
        {
            if (int.TryParse(pskText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shorthand) && shorthand >= 0 && shorthand <= 255)
                settings.Psk = ByteString.CopyFrom(new[] { (byte)shorthand });
            else
                settings.Psk = ByteString.FromBase64(pskText);
        }

        channel.Settings = settings;
        return channel;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
