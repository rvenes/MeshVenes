using System;

namespace MeshVenes.Services;

public static class MqttEndpointPolicy
{
    public static (string Host, int Port, bool UseTls) Resolve(string? address, bool useTls)
    {
        var raw = (address ?? string.Empty).Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "mqtt://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "mqtt" && uri.Scheme != "mqtts") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.AbsolutePath.Length != 0 && uri.AbsolutePath != "/"))
            throw new InvalidOperationException("Use an MQTT host or mqtt(s)://host:port address without credentials or a path.");
        useTls |= uri.Scheme == "mqtts" || string.Equals(uri.Host, "mqtt.meshtastic.org", StringComparison.OrdinalIgnoreCase);
        var port = uri.Port < 0 ? (useTls ? 8883 : 1883) : uri.Port;
        if (port < 1 || port > 65535) throw new InvalidOperationException("Invalid MQTT port.");
        return (uri.Host, port, useTls);
    }
}
