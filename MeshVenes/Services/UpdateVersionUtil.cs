using System;

namespace MeshVenes.Services;

/// <summary>
/// Version parsing shared by the update feed check and the About page.
/// Accepts tags like "v1.4.7", "1.4.7-beta" and "1.4.7+build5".
/// </summary>
public static class UpdateVersionUtil
{
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = StripDecorations(text);
        if (!Version.TryParse(token, out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    public static string SanitizeDisplayVersion(string versionText)
    {
        var token = StripDecorations(versionText);
        return string.IsNullOrWhiteSpace(token) ? versionText : token;
    }

    private static string StripDecorations(string text)
    {
        var token = text.Trim();
        if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            token = token[1..];

        var plusIdx = token.IndexOf('+');
        if (plusIdx >= 0)
            token = token[..plusIdx];

        var dashIdx = token.IndexOf('-');
        if (dashIdx >= 0)
            token = token[..dashIdx];

        return token;
    }
}
