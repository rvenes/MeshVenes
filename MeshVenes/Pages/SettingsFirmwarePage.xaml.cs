using MeshVenes.Models;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System;

namespace MeshVenes.Pages;

public sealed partial class SettingsFirmwarePage : Page
{
    private static readonly HttpClient Http = BuildHttpClient();
    private static readonly Regex VersionRegex = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);
    private static readonly TimeSpan FirmwareCacheTtl = TimeSpan.FromHours(24);

    private string _latestReleaseUrl = "https://github.com/meshtastic/firmware/releases";

    public SettingsFirmwarePage()
    {
        InitializeComponent();
        Loaded += SettingsFirmwarePage_Loaded;
    }

    private async void SettingsFirmwarePage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync(forceOnline: false);

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync(forceOnline: true);

    private async Task LoadAsync(bool forceOnline)
    {
        NodeText.Text = "Configuration for: " + Services.NodeIdentity.ConnectedNodeLabel();
        LastCheckedText.Text = "Checking...";
        LatestStableText.Text = "Loading...";
        UpdateStatusText.Text = forceOnline ? "Checking online firmware release..." : "Checking cached firmware release...";

        var connected = GetConnectedNode();
        HardwareText.Text = connected is null || string.IsNullOrWhiteSpace(connected.HardwareModel) ? "—" : connected.HardwareModel;
        CurrentVersionText.Text = connected is null || string.IsNullOrWhiteSpace(connected.FirmwareVersion) ? "—" : connected.FirmwareVersion;

        try
        {
            if (!forceOnline && TryReadFirmwareCache(out var cached) && IsCacheFresh(cached))
            {
                ApplyReleaseResult(cached.Tag, cached.Url, cached.CheckedUtc, fromCache: true);
                return;
            }

            var release = await GetLatestStableReleaseAsync();
            if (release is null)
            {
                if (TryReadFirmwareCache(out var fallback))
                {
                    ApplyReleaseResult(fallback.Tag, fallback.Url, fallback.CheckedUtc, fromCache: true);
                    UpdateStatusText.Text = "Using cached firmware result (online check unavailable).";
                    return;
                }

                LatestStableText.Text = "Unavailable";
                UpdateStatusText.Text = "Could not read latest firmware from internet.";
                LastCheckedText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }

            var checkedUtc = DateTime.UtcNow;
            WriteFirmwareCache(new FirmwareCacheEntry
            {
                Tag = release.Value.Tag,
                Url = release.Value.Url,
                CheckedUtc = checkedUtc
            });

            ApplyReleaseResult(release.Value.Tag, release.Value.Url, checkedUtc, fromCache: false);
        }
        catch (Exception ex)
        {
            if (TryReadFirmwareCache(out var fallback))
            {
                ApplyReleaseResult(fallback.Tag, fallback.Url, fallback.CheckedUtc, fromCache: true);
                UpdateStatusText.Text = "Using cached firmware result (online check failed: " + ex.Message + ").";
                return;
            }

            LatestStableText.Text = "Unavailable";
            UpdateStatusText.Text = "Firmware check failed: " + ex.Message;
            LastCheckedText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    private void ApplyReleaseResult(string tag, string? url, DateTime checkedUtc, bool fromCache)
    {
        LatestStableText.Text = string.IsNullOrWhiteSpace(tag) ? "Unavailable" : tag;
        _latestReleaseUrl = string.IsNullOrWhiteSpace(url)
            ? "https://github.com/meshtastic/firmware/releases"
            : url!;

        var current = CurrentVersionText.Text;
        if (TryParseVersion(current, out var currentVersion) && TryParseVersion(tag, out var latestVersion))
        {
            var cmp = CompareVersion(currentVersion, latestVersion);
            var baseText = cmp < 0 ? "Update available." : "Your firmware is up to date.";
            UpdateStatusText.Text = fromCache ? baseText + " (cached)" : baseText;
        }
        else
        {
            UpdateStatusText.Text = fromCache
                ? "Unable to compare versions automatically. (cached)"
                : "Unable to compare versions automatically.";
        }

        LastCheckedText.Text = checkedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private NodeLive? GetConnectedNode()
    {
        var idHex = AppState.GetEffectiveAdminTargetNodeIdHex();
        if (string.IsNullOrWhiteSpace(idHex))
            return null;

        return AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(string Tag, string Url)?> GetLatestStableReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/meshtastic/firmware/releases?per_page=20");
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        (string Tag, string Url)? fallback = null;

        foreach (var release in json.RootElement.EnumerateArray())
        {
            var draft = release.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
            var prerelease = release.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
            if (draft)
                continue;

            var tag = release.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? string.Empty) : string.Empty;
            var url = release.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? string.Empty) : string.Empty;

            if (!string.IsNullOrWhiteSpace(tag))
            {
                if (!prerelease)
                    return (tag.Trim(), url.Trim());

                fallback ??= (tag.Trim(), url.Trim());
            }
        }

        return fallback;
    }

    private static HttpClient BuildHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshVenes/1.1");
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    private static bool TryParseVersion(string? text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = VersionRegex.Match(text);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
            return false;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
            return false;
        if (!int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = (major, minor, patch);
        return true;
    }

    private static int CompareVersion((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }

    private static bool IsCacheFresh(FirmwareCacheEntry cache)
        => DateTime.UtcNow - cache.CheckedUtc <= FirmwareCacheTtl;

    private static string FirmwareCachePath
        => Path.Combine(AppDataPaths.BasePath, "firmware-release-cache.json");

    private static bool TryReadFirmwareCache(out FirmwareCacheEntry cache)
    {
        cache = new FirmwareCacheEntry();

        try
        {
            if (!File.Exists(FirmwareCachePath))
                return false;

            var json = File.ReadAllText(FirmwareCachePath);
            var parsed = JsonSerializer.Deserialize<FirmwareCacheEntry>(json);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Tag) || parsed.CheckedUtc == default)
                return false;

            cache = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteFirmwareCache(FirmwareCacheEntry cache)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.BasePath);
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FirmwareCachePath, json);
        }
        catch
        {
        }
    }

    private async void OpenReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_latestReleaseUrl, UriKind.Absolute, out var uri))
            _ = await Launcher.LaunchUriAsync(uri);
    }

    private async void OpenWebFlasher_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate("https://flasher.meshtastic.org/", UriKind.Absolute, out var uri))
            _ = await Launcher.LaunchUriAsync(uri);
    }

    private sealed class FirmwareCacheEntry
    {
        public string Tag { get; set; } = "";
        public string? Url { get; set; }
        public DateTime CheckedUtc { get; set; }
    }
}
