using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

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

    private void GeneratePsk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ChannelRowVm row)
            row.GeneratePskFromPreset();
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
            var desiredChannels = Rows
                .OrderBy(r => r.Index)
                .Select(r => r.ToChannel())
                .ToList();

            StatusText.Text = "Saving channels...";
            var saved = await TrySaveChannelsWithReconnectAsync(nodeNum, desiredChannels);
            if (!saved)
            {
                StatusText.Text = "Failed to save channels.";
                return;
            }

            StatusText.Text = "Verifying channels...";
            var persisted = await EnsureChannelsPersistedAsync(nodeNum, desiredChannels);
            StatusText.Text = persisted
                ? "Channels saved and verified."
                : "Channels saved, but verify failed. Please reload and retry.";

            if (persisted)
            {
                SettingsReconnectHelper.StartPostSaveReconnectWatchdog(
                    text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to save channels: " + ex.Message;
        }
    }

    private async Task<bool> TrySaveChannelsWithReconnectAsync(uint nodeNum, System.Collections.Generic.IReadOnlyList<Channel> channels)
    {
        try
        {
            await AdminConfigClient.Instance.SaveChannelsAsync(nodeNum, channels);
            return true;
        }
        catch (Exception ex) when (SettingsReconnectHelper.IsNotConnectedException(ex))
        {
            StatusText.Text = "Node reboot detected. Connecting...";
            var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(
                text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
            if (!reconnected)
                return false;

            await AdminConfigClient.Instance.SaveChannelsAsync(nodeNum, channels);
            return true;
        }
    }

    private async Task<bool> EnsureChannelsPersistedAsync(uint nodeNum, System.Collections.Generic.IReadOnlyList<Channel> desiredChannels)
    {
        async Task<System.Collections.Generic.IReadOnlyList<Channel>?> LoadCurrentAsync()
        {
            try
            {
                return await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, maxChannels: 8);
            }
            catch (Exception ex) when (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(
                    text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                if (!reconnected)
                    return null;

                return await AdminConfigClient.Instance.GetChannelsAsync(nodeNum, maxChannels: 8);
            }
        }

        var current = await LoadCurrentAsync();
        if (current is null)
            return false;

        if (ChannelsEquivalent(desiredChannels, current))
            return true;

        StatusText.Text = "Channels differ after save. Applying once more...";
        var reapplied = await TrySaveChannelsWithReconnectAsync(nodeNum, desiredChannels);
        if (!reapplied)
            return false;

        current = await LoadCurrentAsync();
        return current is not null && ChannelsEquivalent(desiredChannels, current);
    }

    private static bool ChannelsEquivalent(
        System.Collections.Generic.IReadOnlyList<Channel> desired,
        System.Collections.Generic.IReadOnlyList<Channel> current)
    {
        var desiredByIndex = desired
            .GroupBy(c => c?.Index ?? -1)
            .ToDictionary(g => g.Key, g => g.First());

        var currentByIndex = current
            .GroupBy(c => c?.Index ?? -1)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var index in Enumerable.Range(0, 8))
        {
            desiredByIndex.TryGetValue(index, out var d);
            currentByIndex.TryGetValue(index, out var c);
            if (!ChannelEquivalent(d, c))
                return false;
        }

        return true;
    }

    private static bool ChannelEquivalent(Channel? desired, Channel? current)
    {
        var desiredRole = desired?.Role ?? Channel.Types.Role.Disabled;
        var currentRole = current?.Role ?? Channel.Types.Role.Disabled;
        if (desiredRole != currentRole)
            return false;

        if (desiredRole == Channel.Types.Role.Disabled)
            return true;

        var desiredSettings = desired?.Settings;
        var currentSettings = current?.Settings;

        static string Norm(string? value) => (value ?? "").Trim();

        if (!string.Equals(Norm(desiredSettings?.Name), Norm(currentSettings?.Name), StringComparison.Ordinal))
            return false;

        if ((desiredSettings?.Id ?? 0) != (currentSettings?.Id ?? 0))
            return false;

        if ((desiredSettings?.UplinkEnabled ?? false) != (currentSettings?.UplinkEnabled ?? false))
            return false;

        if ((desiredSettings?.DownlinkEnabled ?? false) != (currentSettings?.DownlinkEnabled ?? false))
            return false;

        var desiredPsk = desiredSettings?.Psk?.ToByteArray() ?? Array.Empty<byte>();
        var currentPsk = currentSettings?.Psk?.ToByteArray() ?? Array.Empty<byte>();
        if (!desiredPsk.SequenceEqual(currentPsk))
            return false;

        var desiredModule = desiredSettings?.ModuleSettings;
        var currentModule = currentSettings?.ModuleSettings;

        if ((desiredModule?.PositionPrecision ?? 0) != (currentModule?.PositionPrecision ?? 0))
            return false;

        if ((desiredModule?.IsMuted ?? false) != (currentModule?.IsMuted ?? false))
            return false;

        return true;
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
            if (_roleIndex == 0)
            {
                _allowPositionRequests = false;
                _preciseLocation = false;
            }
            EnsurePreciseLocationIsAllowed();
            OnChanged();
            OnChanged(nameof(CanUsePreciseLocation));
            OnChanged(nameof(IsApproximateControlEnabled));
            OnChanged(nameof(ApproximatePositionLabel));
        }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value ?? ""; OnChanged(); }
    }

    private string _pskBase64 = "";
    private bool _enableAutoSecondaryOnPskInput;
    public string PskBase64
    {
        get => _pskBase64;
        set
        {
            if (_pskBase64 == value) return;
            _pskBase64 = value ?? "";
            if (_enableAutoSecondaryOnPskInput)
                AutoSetSecondaryWhenPskEntered();
            _pskPresetIndex = DetectPskPresetIndex(_pskBase64);
            EnsurePreciseLocationIsAllowed();
            OnChanged();
            OnChanged(nameof(PskPresetIndex));
            OnChanged(nameof(CanUsePreciseLocation));
            OnChanged(nameof(IsApproximateControlEnabled));
        }
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

    private int _pskPresetIndex = 0;
    public int PskPresetIndex
    {
        get => _pskPresetIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 4);
            if (_pskPresetIndex == clamped) return;
            _pskPresetIndex = clamped;
            OnChanged();
        }
    }

    private bool _allowPositionRequests;
    public bool AllowPositionRequests
    {
        get => _allowPositionRequests;
        set
        {
            if (_allowPositionRequests == value) return;
            _allowPositionRequests = value;
            if (!_allowPositionRequests)
                _preciseLocation = false;
            else if (_approximatePositionPrecision < 12 || _approximatePositionPrecision > 15)
                _approximatePositionPrecision = 14;

            EnsurePreciseLocationIsAllowed();
            OnChanged();
            OnChanged(nameof(PreciseLocation));
            OnChanged(nameof(IsApproximateControlEnabled));
            OnChanged(nameof(ApproximatePositionLabel));
        }
    }

    private bool _preciseLocation;
    public bool PreciseLocation
    {
        get => _preciseLocation;
        set
        {
            if (_preciseLocation == value) return;
            _preciseLocation = value;
            if (_preciseLocation)
            {
                _allowPositionRequests = true;
                EnsurePreciseLocationIsAllowed();
            }

            OnChanged();
            OnChanged(nameof(AllowPositionRequests));
            OnChanged(nameof(IsApproximateControlEnabled));
            OnChanged(nameof(ApproximatePositionLabel));
        }
    }

    private double _approximatePositionPrecision = 14;
    public double ApproximatePositionPrecision
    {
        get => _approximatePositionPrecision;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 12, 15);
            if (Math.Abs(_approximatePositionPrecision - clamped) < 0.001)
                return;

            _approximatePositionPrecision = clamped;
            OnChanged();
            OnChanged(nameof(ApproximatePositionLabel));
        }
    }

    public bool CanUsePreciseLocation => IsRoleEnabled && IsPskEligibleForPreciseLocation();
    public bool IsApproximateControlEnabled => AllowPositionRequests && !PreciseLocation;

    public string ApproximatePositionLabel
    {
        get
        {
            if (!AllowPositionRequests)
                return "Disabled";

            if (PreciseLocation)
                return "Precise";

            var precision = (int)Math.Round(ApproximatePositionPrecision);
            return precision switch
            {
                12 => "Within ~11 km",
                13 => "Within ~3.5 km",
                14 => "Within ~1.5 km",
                15 => "Within ~700 m",
                _ => $"Precision {precision}"
            };
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted == value) return; _isMuted = value; OnChanged(); }
    }

    public static ChannelRowVm FromChannel(Channel channel)
    {
        var settings = channel.Settings;
        var moduleSettings = settings?.ModuleSettings;
        var rawPrecision = moduleSettings?.PositionPrecision ?? 0;
        var allowPositionRequests = rawPrecision > 0;
        var preciseLocation = rawPrecision == 32;
        var approximatePrecision = rawPrecision >= 12 && rawPrecision <= 15 ? rawPrecision : 14;

        if (!allowPositionRequests)
        {
            preciseLocation = false;
            approximatePrecision = 14;
        }

        var vm = new ChannelRowVm
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
            DownlinkEnabled = settings?.DownlinkEnabled ?? false,
            AllowPositionRequests = allowPositionRequests,
            PreciseLocation = preciseLocation,
            ApproximatePositionPrecision = approximatePrecision,
            IsMuted = moduleSettings?.IsMuted ?? false
        };

        vm._enableAutoSecondaryOnPskInput = true;
        return vm;
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
            DownlinkEnabled = DownlinkEnabled,
            ModuleSettings = new ModuleSettings
            {
                PositionPrecision = GetPositionPrecisionForSave(),
                IsMuted = IsMuted
            }
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

    public void GeneratePskFromPreset()
    {
        switch (PskPresetIndex)
        {
            case 0:
                PskBase64 = "";
                break;
            case 1:
                PskBase64 = "AQ==";
                break;
            case 2:
                PskBase64 = GenerateRandomBase64(1);
                break;
            case 3:
                PskBase64 = GenerateRandomBase64(16);
                break;
            case 4:
                PskBase64 = GenerateRandomBase64(32);
                break;
            default:
                PskBase64 = "";
                break;
        }
    }

    private static string GenerateRandomBase64(int bytes)
    {
        if (bytes <= 0)
            return "";

        var data = new byte[bytes];
        RandomNumberGenerator.Fill(data);
        return Convert.ToBase64String(data);
    }

    private static int DetectPskPresetIndex(string pskBase64)
    {
        var text = (pskBase64 ?? "").Trim();
        if (text.Length == 0)
            return 0;

        if (string.Equals(text, "AQ==", StringComparison.Ordinal))
            return 1;

        try
        {
            var decoded = Convert.FromBase64String(text);
            return decoded.Length switch
            {
                1 => 2,
                16 => 3,
                32 => 4,
                _ => 3
            };
        }
        catch
        {
            return 3;
        }
    }

    private void AutoSetSecondaryWhenPskEntered()
    {
        if (Index <= 0)
            return;

        if (_roleIndex == 0 && !string.IsNullOrWhiteSpace(_pskBase64))
        {
            _roleIndex = 2;
            OnChanged(nameof(RoleIndex));
        }
    }

    private bool IsRoleEnabled => _roleIndex == 1 || _roleIndex == 2;

    private bool IsPskEligibleForPreciseLocation()
    {
        var text = (_pskBase64 ?? "").Trim();
        if (text.Length == 0 || string.Equals(text, "AQ==", StringComparison.Ordinal))
            return false;

        try
        {
            var decoded = Convert.FromBase64String(text);
            return decoded.Length > 1;
        }
        catch
        {
            return false;
        }
    }

    private void EnsurePreciseLocationIsAllowed()
    {
        if (!CanUsePreciseLocation && _preciseLocation)
        {
            _preciseLocation = false;
            OnChanged(nameof(PreciseLocation));
        }
    }

    private uint GetPositionPrecisionForSave()
    {
        if (!AllowPositionRequests)
            return 0;

        if (PreciseLocation && CanUsePreciseLocation)
            return 32;

        return (uint)Math.Clamp((int)Math.Round(ApproximatePositionPrecision), 12, 15);
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
