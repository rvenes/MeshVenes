using Google.Protobuf;
using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshVenes.Services;

/// <summary>
/// Builds and parses the standard Meshtastic channel share URL
/// (https://meshtastic.org/e/#base64url-of-ChannelSet) used by QR codes and
/// DeviceProfile.channel_url in the official clients.
/// </summary>
public static class ChannelUrlUtil
{
    public static string BuildShareUrl(ChannelSet channelSet, bool addMode = false)
    {
        if (channelSet is null)
            throw new ArgumentNullException(nameof(channelSet));

        var base64url = ToBase64Url(Convert.ToBase64String(channelSet.ToByteArray()));
        var addPart = addMode ? "?add=true" : string.Empty;
        return $"https://meshtastic.org/e/{addPart}#{base64url}";
    }

    public static bool TryParseShareUrl(string? text, out ChannelSet channelSet, out bool addMode)
    {
        channelSet = new ChannelSet();
        addMode = false;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var source = text.Trim();
        string payload;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            payload = uri.Fragment?.StartsWith("#") == true ? uri.Fragment.Substring(1) : "";
            var query = uri.Query ?? "";
            addMode = query.IndexOf("add=true", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        else
        {
            var hash = source.LastIndexOf('#');
            payload = hash >= 0 ? source[(hash + 1)..] : source;
        }

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            var data = Convert.FromBase64String(FromBase64Url(payload));
            channelSet = ChannelSet.Parser.ParseFrom(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maps a ChannelSet to a full 8-slot channel table: slot 0 becomes Primary,
    /// following settings become Secondary, remaining slots are Disabled.
    /// </summary>
    public static List<Channel> ToReplacementChannels(IEnumerable<ChannelSettings> settings)
    {
        var result = new List<Channel>(8);
        for (var i = 0; i < 8; i++)
            result.Add(new Channel { Index = i, Role = Channel.Types.Role.Disabled });

        var list = settings?.ToList() ?? new List<ChannelSettings>();
        for (var i = 0; i < list.Count && i < 8; i++)
        {
            result[i] = new Channel
            {
                Index = i,
                Role = i == 0 ? Channel.Types.Role.Primary : Channel.Types.Role.Secondary,
                Settings = list[i].Clone()
            };
        }

        return result;
    }

    public static string ToBase64Url(string base64)
        => base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string FromBase64Url(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
    }
}
