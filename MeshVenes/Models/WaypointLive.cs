using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MeshVenes.Models;

public sealed class WaypointLive : INotifyPropertyChanged
{
    private const string DefaultGlyph = "📍";

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint WaypointId { get; }

    private uint _sourceNodeNum;
    public uint SourceNodeNum
    {
        get => _sourceNodeNum;
        set => Set(ref _sourceNodeNum, value);
    }

    private string _sourceIdHex = "";
    public string SourceIdHex
    {
        get => _sourceIdHex;
        set => Set(ref _sourceIdHex, value ?? "");
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value?.Trim() ?? ""))
                return;

            OnPropertyChanged(nameof(DisplayName));
        }
    }

    private string _description = "";
    public string Description
    {
        get => _description;
        set => Set(ref _description, value?.Trim() ?? "");
    }

    private double _latitude;
    public double Latitude
    {
        get => _latitude;
        set
        {
            if (!Set(ref _latitude, value))
                return;

            OnPropertyChanged(nameof(HasPosition));
        }
    }

    private double _longitude;
    public double Longitude
    {
        get => _longitude;
        set
        {
            if (!Set(ref _longitude, value))
                return;

            OnPropertyChanged(nameof(HasPosition));
        }
    }

    private uint _iconCodepoint;
    public uint IconCodepoint
    {
        get => _iconCodepoint;
        set
        {
            if (!Set(ref _iconCodepoint, value))
                return;

            OnPropertyChanged(nameof(IconGlyph));
        }
    }

    private uint _lockedToNodeNum;
    public uint LockedToNodeNum
    {
        get => _lockedToNodeNum;
        set => Set(ref _lockedToNodeNum, value);
    }

    private DateTime? _expireUtc;
    public DateTime? ExpireUtc
    {
        get => _expireUtc;
        set
        {
            if (!Set(ref _expireUtc, value))
                return;

            OnPropertyChanged(nameof(IsExpired));
            OnPropertyChanged(nameof(ExpireUnixUtc));
        }
    }

    private DateTime _lastUpdatedUtc = DateTime.UtcNow;
    public DateTime LastUpdatedUtc
    {
        get => _lastUpdatedUtc;
        set => Set(ref _lastUpdatedUtc, value);
    }

    private uint _channelIndex;
    public uint ChannelIndex
    {
        get => _channelIndex;
        set => Set(ref _channelIndex, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Waypoint {WaypointId:x8}" : Name;

    public string IconGlyph => ToGlyph(IconCodepoint);

    public bool HasPosition => IsValidCoordinate(Latitude, Longitude);

    public bool IsExpired => ExpireUtc.HasValue && ExpireUtc.Value <= DateTime.UtcNow;

    public long? ExpireUnixUtc => ExpireUtc.HasValue
        ? new DateTimeOffset(DateTime.SpecifyKind(ExpireUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()
        : null;

    public WaypointLive(uint waypointId)
    {
        WaypointId = waypointId;
    }

    public void ApplyFrom(WaypointLive other)
    {
        if (other is null || other.WaypointId != WaypointId)
            return;

        SourceNodeNum = other.SourceNodeNum;
        SourceIdHex = other.SourceIdHex;
        Name = other.Name;
        Description = other.Description;
        Latitude = other.Latitude;
        Longitude = other.Longitude;
        IconCodepoint = other.IconCodepoint;
        LockedToNodeNum = other.LockedToNodeNum;
        ExpireUtc = other.ExpireUtc;
        LastUpdatedUtc = other.LastUpdatedUtc;
        ChannelIndex = other.ChannelIndex;
    }

    public static string ToGlyph(uint codepoint)
    {
        if (codepoint == 0)
            return DefaultGlyph;

        if (codepoint > 0x10FFFF)
            return DefaultGlyph;

        try
        {
            return char.ConvertFromUtf32((int)codepoint);
        }
        catch
        {
            return DefaultGlyph;
        }
    }

    public static uint ParseCodepointFromGlyph(string? glyph)
    {
        if (string.IsNullOrWhiteSpace(glyph))
            return 0;

        var text = glyph.Trim();
        var rune = text.EnumerateRunes().FirstOrDefault();
        return rune.Value > 0 ? (uint)rune.Value : 0;
    }

    private static bool IsValidCoordinate(double latitude, double longitude)
        => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
