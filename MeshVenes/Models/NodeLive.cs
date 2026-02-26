using System;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeshVenes.Models;

public sealed class NodeLive : INotifyPropertyChanged
{
    private static readonly Brush LoRaGoodBrush = new SolidColorBrush(Colors.LimeGreen);
    private static readonly Brush LoRaOkBrush = new SolidColorBrush(Colors.Goldenrod);
    private static readonly Brush LoRaWeakBrush = new SolidColorBrush(Colors.OrangeRed);
    private static readonly Brush UnknownLinkBrush = new SolidColorBrush(Colors.Gray);
    private static readonly Brush MqttBrush = new SolidColorBrush(Colors.DeepSkyBlue);
    private static readonly Brush FavoriteOnBrush = new SolidColorBrush(Colors.Gold);
    private static readonly Brush FavoriteOffBrush = new SolidColorBrush(Colors.DimGray);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IdHex { get; }

    // Used for sorting/filtering.
    public DateTime LastHeardUtc { get; private set; } = DateTime.MinValue;
    public DateTime FirstHeardUtc { get; private set; } = DateTime.MinValue;

    // Optional (can be shown in UI).
    private ulong _nodeNum;
    public ulong NodeNum
    {
        get => _nodeNum;
        set
        {
            if (_nodeNum == value) return;
            _nodeNum = value;
            OnChanged(nameof(NodeNum));
        }
    }

    private string _userId = "";
    public string UserId
    {
        get => _userId;
        set
        {
            if (_userId == value) return;
            _userId = value;
            OnChanged(nameof(UserId));
        }
    }

    private string _publicKey = "";
    public string PublicKey
    {
        get => _publicKey;
        set
        {
            if (_publicKey == value) return;
            _publicKey = value;
            OnChanged(nameof(PublicKey));
        }
    }

    private string _firmwareVersion = "";
    public string FirmwareVersion
    {
        get => _firmwareVersion;
        set
        {
            if (_firmwareVersion == value) return;
            _firmwareVersion = value;
            OnChanged(nameof(FirmwareVersion));
        }
    }

    private string _role = "";
    public string Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            OnChanged(nameof(Role));
            OnChanged(nameof(IsUnmonitored));
            OnChanged(nameof(UnmonitoredVisibility));
            OnChanged(nameof(UnmonitoredBadgeText));
            OnChanged(nameof(UnmonitoredBrush));
        }
    }

    private string _hardwareModel = "";
    public string HardwareModel
    {
        get => _hardwareModel;
        set
        {
            if (_hardwareModel == value) return;
            _hardwareModel = value;
            OnChanged(nameof(HardwareModel));
        }
    }

    private uint? _uptimeSeconds;
    public uint? UptimeSeconds
    {
        get => _uptimeSeconds;
        set
        {
            if (_uptimeSeconds == value) return;
            _uptimeSeconds = value;
            OnChanged(nameof(UptimeSeconds));
        }
    }

    public string ShortId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IdHex))
                return "";

            var s = IdHex.Trim();

            if (s.StartsWith("fr=", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(3).Trim();

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            if (s.Length >= 4)
                return s[^4..].ToUpperInvariant();

            return s.ToUpperInvariant();
        }
    }

    private string _longName = "";
    public string LongName
    {
        get => _longName;
        set
        {
            if (_longName != value)
            {
                _longName = value;
                OnChanged(nameof(LongName));
                OnChanged(nameof(Name));
                OnChanged(nameof(SortNameKey));
            }
        }
    }

    private string _shortName = "";
    public string ShortName
    {
        get => _shortName;
        set
        {
            if (_shortName != value)
            {
                _shortName = value;
                OnChanged(nameof(ShortName));
                OnChanged(nameof(Name));
                OnChanged(nameof(SortNameKey));
            }
        }
    }

    private string _sub = "";
    public string Sub
    {
        get => _sub;
        set { if (_sub != value) { _sub = value; OnChanged(nameof(Sub)); } }
    }

    private string _snr = "—";
    public string SNR
    {
        get => _snr;
        set
        {
            if (_snr == value) return;
            _snr = value;
            OnChanged(nameof(SNR));
            OnSignalPresentationChanged();
        }
    }

    private string _rssi = "—";
    public string RSSI
    {
        get => _rssi;
        set
        {
            if (_rssi == value) return;
            _rssi = value;
            OnChanged(nameof(RSSI));
            OnSignalPresentationChanged();
        }
    }

    private bool? _viaMqtt;
    public bool? ViaMqtt
    {
        get => _viaMqtt;
        set
        {
            if (_viaMqtt == value) return;
            _viaMqtt = value;
            OnChanged(nameof(ViaMqtt));
            OnSignalPresentationChanged();
        }
    }

    public string TransportText => ViaMqtt switch
    {
        true => "MQTT",
        false => "LoRa",
        _ => "—"
    };

    public string SignalQualityText
    {
        get
        {
            if (ViaMqtt == true)
                return "MQTT";

            if (!TryParseRssi(out var rssi))
                return "No link";

            if (rssi >= -85)
                return "Good";

            if (rssi >= -100)
                return "OK";

            return "Weak";
        }
    }

    public string TransportBadgeText => ViaMqtt switch
    {
        true => "MQTT",
        false => $"📶 {SignalQualityText}",
        _ => "—"
    };

    public Brush TransportBrush => ViaMqtt switch
    {
        true => MqttBrush,
        false => SignalQualityText switch
        {
            "Good" => LoRaGoodBrush,
            "OK" => LoRaOkBrush,
            "Weak" => LoRaWeakBrush,
            _ => UnknownLinkBrush
        },
        _ => UnknownLinkBrush
    };

    public string SignalDetailsText => ViaMqtt switch
    {
        true => "MQTT relay",
        false => $"{SignalQualityText} • RSSI {FormatMetric(RSSI)} • SNR {FormatMetric(SNR)}",
        _ => "—"
    };

    private int? _hopsAway;
    public int? HopsAway
    {
        get => _hopsAway;
        set
        {
            var normalized = value.HasValue && value.Value >= 0 ? value : null;
            if (_hopsAway == normalized) return;
            _hopsAway = normalized;
            OnChanged(nameof(HopsAway));
            OnChanged(nameof(HopsAwayText));
            OnChanged(nameof(HopsBadgeText));
        }
    }

    public string HopsAwayText => HopsAway?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string HopsBadgeText => $"Hops: {HopsAwayText}";

    private bool _isIgnored;
    public bool IsIgnored
    {
        get => _isIgnored;
        set
        {
            if (_isIgnored == value) return;
            _isIgnored = value;
            OnChanged(nameof(IsIgnored));
        }
    }

    public bool IsUnmonitored =>
        !string.IsNullOrWhiteSpace(Role) &&
        Role.Contains("unmonitored", StringComparison.OrdinalIgnoreCase);

    public Visibility UnmonitoredVisibility => IsUnmonitored ? Visibility.Visible : Visibility.Collapsed;
    public string UnmonitoredBadgeText => IsUnmonitored ? "Unmonitored" : "";
    public Brush UnmonitoredBrush => IsUnmonitored ? LoRaWeakBrush : UnknownLinkBrush;

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnChanged(nameof(IsFavorite));
            OnChanged(nameof(FavoriteGlyph));
            OnChanged(nameof(FavoriteBrush));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public Brush FavoriteBrush => IsFavorite ? FavoriteOnBrush : FavoriteOffBrush;

    private string _lastHeard = "—";
    public string LastHeard
    {
        get => _lastHeard;
        set { if (_lastHeard != value) { _lastHeard = value; OnChanged(nameof(LastHeard)); } }
    }

    // GPS fields.
    private double _lat;
    public double Latitude { get => _lat; set { if (_lat != value) { _lat = value; OnChanged(nameof(Latitude)); OnChanged(nameof(HasPosition)); } } }

    private double _lon;
    public double Longitude { get => _lon; set { if (_lon != value) { _lon = value; OnChanged(nameof(Longitude)); OnChanged(nameof(HasPosition)); } } }

    private DateTime _lastPosUtc = DateTime.MinValue;
    public DateTime LastPositionUtc
    {
        get => _lastPosUtc;
        set
        {
            if (_lastPosUtc == value) return;
            _lastPosUtc = value;
            OnChanged(nameof(LastPositionUtc));
            OnChanged(nameof(LastPositionText));
            OnChanged(nameof(HasPosition));
            OnChanged(nameof(PositionIconVisibility));
        }
    }

    public bool HasPosition => LastPositionUtc != DateTime.MinValue;
    public Visibility PositionIconVisibility => HasPosition ? Visibility.Visible : Visibility.Collapsed;

    public string LastPositionText
    {
        get
        {
            if (!HasPosition) return "—";
            var local = LastPositionUtc.ToLocalTime();
            return local.ToString("HH:mm:ss");
        }
    }

    public bool UpdatePosition(double lat, double lon, DateTime tsUtc, double? alt = null)
    {
        // Ignore invalid/default coordinates so nodes are not shown at 0,0.
        if (double.IsNaN(lat) || double.IsInfinity(lat) ||
            double.IsNaN(lon) || double.IsInfinity(lon) ||
            lat < -90 || lat > 90 ||
            lon < -180 || lon > 180 ||
            (Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001))
        {
            return false;
        }

        Latitude = lat;
        Longitude = lon;
        LastPositionUtc = tsUtc;
        return true;
    }

    private double? _distanceFromConnectedKm;
    public double? DistanceFromConnectedKm => _distanceFromConnectedKm;

    private int? _bearingFromConnectedDegrees;
    public int? BearingFromConnectedDegrees => _bearingFromConnectedDegrees;

    public Visibility DistanceDirectionVisibility => _distanceFromConnectedKm.HasValue ? Visibility.Visible : Visibility.Collapsed;

    public string DistanceDirectionText
    {
        get
        {
            if (!_distanceFromConnectedKm.HasValue)
                return "—";

            var distanceText = FormatDistanceKm(_distanceFromConnectedKm.Value);
            if (!_bearingFromConnectedDegrees.HasValue)
                return distanceText;

            var bearing = NormalizeBearing(_bearingFromConnectedDegrees.Value);
            var arrow = BearingToArrow(bearing);
            return $"{distanceText} {arrow} {bearing.ToString(CultureInfo.InvariantCulture)}°";
        }
    }

    public void SetDistanceAndBearing(double? distanceKm, int? bearingDegrees)
    {
        var normalizedDistance = distanceKm.HasValue && distanceKm.Value >= 0 ? distanceKm : null;
        int? normalizedBearing = null;
        if (bearingDegrees.HasValue)
            normalizedBearing = NormalizeBearing(bearingDegrees.Value);

        if (_distanceFromConnectedKm == normalizedDistance && _bearingFromConnectedDegrees == normalizedBearing)
            return;

        _distanceFromConnectedKm = normalizedDistance;
        _bearingFromConnectedDegrees = normalizedBearing;
        OnChanged(nameof(DistanceFromConnectedKm));
        OnChanged(nameof(BearingFromConnectedDegrees));
        OnChanged(nameof(DistanceDirectionVisibility));
        OnChanged(nameof(DistanceDirectionText));
    }

    public bool HasUnread => MeshVenes.AppState.HasUnread(IdHex);

    public Visibility UnreadVisible => HasUnread ? Visibility.Visible : Visibility.Collapsed;

    private bool _hasLogIndicator;
    public bool HasLogIndicator
    {
        get => _hasLogIndicator;
        set
        {
            if (_hasLogIndicator == value) return;
            _hasLogIndicator = value;
            OnChanged(nameof(HasLogIndicator));
            OnChanged(nameof(LogIndicatorVisible));
        }
    }

    public Visibility LogIndicatorVisible => HasLogIndicator ? Visibility.Visible : Visibility.Collapsed;

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnChanged(nameof(IsVisible));
            OnChanged(nameof(NodeVisibility));
        }
    }

    public Visibility NodeVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

    // Prefer name from NodeInfo when available, otherwise fall back to ShortId/IdHex.
    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LongName))
                return LongName;
            if (!string.IsNullOrWhiteSpace(ShortName))
                return ShortName;
            if (!string.IsNullOrWhiteSpace(ShortId))
                return ShortId;
            return IdHex;
        }
    }

    public string SortNameKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LongName))
                return LongName.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(ShortName))
                return ShortName.ToUpperInvariant();
            return (IdHex ?? "").ToUpperInvariant();
        }
    }

    public string SortIdKey => (IdHex ?? "").ToUpperInvariant();

    public NodeLive(string idHex)
    {
        IdHex = idHex;
        Sub = "Seen on mesh";
        SetFirstHeard(DateTime.UtcNow);

        // Update unread indicator when AppState changes.
        MeshVenes.AppState.UnreadChanged += peer =>
        {
            if (string.IsNullOrWhiteSpace(peer))
                return;

            if (string.Equals(peer, IdHex, StringComparison.OrdinalIgnoreCase))
            {
                OnChanged(nameof(HasUnread));
                OnChanged(nameof(UnreadVisible));
            }
        };

        Touch();
    }

    public void SetFirstHeard(DateTime tsUtc)
    {
        if (tsUtc == DateTime.MinValue)
            return;

        if (FirstHeardUtc == DateTime.MinValue || tsUtc < FirstHeardUtc)
        {
            FirstHeardUtc = tsUtc;
            OnChanged(nameof(FirstHeardUtc));
        }
    }

    public void Touch()
    {
        var nowUtc = DateTime.UtcNow;
        SetFirstHeard(nowUtc);
        LastHeardUtc = nowUtc;
        LastHeard = nowUtc.ToLocalTime().ToString("HH:mm:ss");
        OnChanged(nameof(LastHeardUtc));
    }

    private static string FormatMetric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "—";
        return value.Trim();
    }

    private static int NormalizeBearing(int bearingDegrees)
    {
        var normalized = bearingDegrees % 360;
        if (normalized < 0)
            normalized += 360;
        return normalized;
    }

    private static string FormatDistanceKm(double distanceKm)
    {
        if (distanceKm < 1)
        {
            var meters = Math.Round(distanceKm * 1000.0);
            return $"{meters.ToString("0", CultureInfo.InvariantCulture)} m";
        }

        if (distanceKm < 10)
            return $"{distanceKm.ToString("0.0", CultureInfo.InvariantCulture)} km";

        return $"{Math.Round(distanceKm).ToString("0", CultureInfo.InvariantCulture)} km";
    }

    private static string BearingToArrow(int bearingDegrees)
    {
        var b = NormalizeBearing(bearingDegrees);
        if (b >= 338 || b < 23) return "↑";
        if (b < 68) return "↗";
        if (b < 113) return "→";
        if (b < 158) return "↘";
        if (b < 203) return "↓";
        if (b < 248) return "↙";
        if (b < 293) return "←";
        return "↖";
    }

    private bool TryParseRssi(out int rssi)
    {
        rssi = 0;
        if (string.IsNullOrWhiteSpace(RSSI) || RSSI == "—")
            return false;
        if (!int.TryParse(RSSI, out var parsed))
            return false;
        if (parsed == 0)
            return false;
        rssi = parsed;
        return true;
    }

    private void OnSignalPresentationChanged()
    {
        OnChanged(nameof(TransportText));
        OnChanged(nameof(SignalQualityText));
        OnChanged(nameof(TransportBadgeText));
        OnChanged(nameof(TransportBrush));
        OnChanged(nameof(SignalDetailsText));
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
