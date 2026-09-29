using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MeshVenes.Services;

public static class UpdateSourcePolicy
{
    public static void ValidatePackage(string version, string url, string sha256, long sizeBytes, bool localTest = false)
    {
        if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") ||
            !Version.TryParse(version, out _))
            throw new InvalidDataException("Invalid update version.");
        if (!Regex.IsMatch(sha256, @"\A[a-f0-9]{64}\z") || sizeBytes <= 0 || sizeBytes > 1024L * 1024 * 1024)
            throw new InvalidDataException("Invalid update integrity metadata.");
        var expected = $"https://venes.org/meshvenes/MeshVenes-{version}-win-x64.zip";
        if (!string.Equals(url, expected, StringComparison.Ordinal) && !(localTest && IsLoopbackHttp(url)))
            throw new InvalidDataException("The update package must use the official HTTPS download URL.");
    }

    public static bool IsLoopbackHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        (uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "localhost") &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public static string? SafeReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.StartsWith("/rvenes/MeshVenes/releases/", StringComparison.Ordinal)
            ? url : null;
}
