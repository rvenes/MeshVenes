using MeshtasticWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsFirmwarePage : Page
{
    private static readonly HttpClient Http = BuildHttpClient();
    private static readonly Regex VersionRegex = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private string _latestReleaseUrl = "https://github.com/meshtastic/firmware/releases";

    public SettingsFirmwarePage()
    {
        InitializeComponent();
        Loaded += SettingsFirmwarePage_Loaded;
    }

    private async void SettingsFirmwarePage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + Services.NodeIdentity.ConnectedNodeLabel();
        LastCheckedText.Text = "Checking...";
        LatestStableText.Text = "Loading...";
        UpdateStatusText.Text = "Checking online firmware release...";

        var connected = GetConnectedNode();
        HardwareText.Text = connected is null || string.IsNullOrWhiteSpace(connected.HardwareModel) ? "—" : connected.HardwareModel;
        CurrentVersionText.Text = connected is null || string.IsNullOrWhiteSpace(connected.FirmwareVersion) ? "—" : connected.FirmwareVersion;

        try
        {
            var release = await GetLatestStableReleaseAsync();
            if (release is null)
            {
                LatestStableText.Text = "Unavailable";
                UpdateStatusText.Text = "Could not read latest firmware from internet.";
                LastCheckedText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }

            LatestStableText.Text = release.Value.Tag;
            _latestReleaseUrl = string.IsNullOrWhiteSpace(release.Value.Url)
                ? "https://github.com/meshtastic/firmware/releases"
                : release.Value.Url;

            var current = CurrentVersionText.Text;
            if (TryParseVersion(current, out var currentVersion) && TryParseVersion(release.Value.Tag, out var latestVersion))
            {
                var cmp = CompareVersion(currentVersion, latestVersion);
                UpdateStatusText.Text = cmp < 0
                    ? "Update available."
                    : "Your firmware is up to date.";
            }
            else
            {
                UpdateStatusText.Text = "Unable to compare versions automatically.";
            }

            LastCheckedText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LatestStableText.Text = "Unavailable";
            UpdateStatusText.Text = "Firmware check failed: " + ex.Message;
            LastCheckedText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    private NodeLive? GetConnectedNode()
    {
        var idHex = AppState.ConnectedNodeIdHex;
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshtasticWin/1.1");
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
}
