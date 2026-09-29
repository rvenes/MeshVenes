using System;

namespace MeshVenes.Services;

public static class MapSourcePolicy
{
    public static bool IsTrustedPage(string? source, string host) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == host && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/Map/map.html" &&
        string.IsNullOrEmpty(uri.Query);
}
