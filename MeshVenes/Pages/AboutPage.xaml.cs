using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace MeshVenes.Pages;

public sealed partial class AboutPage : Page
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/rvenes/MeshVenes/releases/latest";
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
    }

    public string VersionText => $"Version: {ResolveVersion()}";

    private async void AboutPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        EnsurePayPalQrImageSource();
        await CheckForUpdatesAsync();
    }

    private void PayPalQrImage_ImageFailed(object sender, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e)
    {
        EnsurePayPalQrImageSource();
    }

    private void EnsurePayPalQrImageSource()
    {
        if (TrySetPayPalQrFromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "paypalqr.png")))
            return;

        if (TrySetPayPalQrFromPath(Path.Combine(AppContext.BaseDirectory, "paypalqr.png")))
            return;

        if (TrySetPayPalQrFromUri("ms-appx:///Assets/paypalqr.png"))
            return;
    }

    private bool TrySetPayPalQrFromPath(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            PayPalQrImage.Source = new BitmapImage(new Uri(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySetPayPalQrFromUri(string uriText)
    {
        try
        {
            PayPalQrImage.Source = new BitmapImage(new Uri(uriText));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var currentVersion = ResolveVersion();
        try
        {
            using var response = await UpdateHttpClient.GetAsync(LatestReleaseApiUrl);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            var latest = await JsonSerializer.DeserializeAsync<LatestReleaseDto>(contentStream);
            if (latest is null || string.IsNullOrWhiteSpace(latest.TagName))
            {
                UpdateStatusText.Text = "Update status: unable to read latest release information.";
                return;
            }

            if (!TryParseVersion(currentVersion, out var current) || !TryParseVersion(latest.TagName, out var remote))
            {
                UpdateStatusText.Text = $"Update status: latest on GitHub is {latest.TagName}.";
                SetReleaseLink(latest.HtmlUrl);
                SetDownloadLink(latest);
                SetReleaseNotes(latest);
                return;
            }

            if (remote > current)
            {
                UpdateStatusText.Text = $"Update status: new version available ({latest.TagName}).";
                SetReleaseLink(latest.HtmlUrl);
                SetDownloadLink(latest);
                SetReleaseNotes(latest);
            }
            else
            {
                UpdateStatusText.Text = $"Update status: you are up to date ({currentVersion}).";
                UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                DownloadUpdateButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                ReleaseNotesText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update status: check failed ({ex.Message}).";
            UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            DownloadUpdateButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            ReleaseNotesText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshVenes/1.0 (+https://github.com/rvenes/MeshVenes)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private void SetReleaseLink(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }

        UpdateLinkButton.NavigateUri = new Uri(releaseUrl);
        UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void SetDownloadLink(LatestReleaseDto latest)
    {
        var asset = latest.Assets?
            .Where(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .OrderByDescending(a => IsPreferredUpdateAsset(a.Name))
            .ThenBy(a => a.Name ?? "")
            .FirstOrDefault();

        if (asset is null)
        {
            DownloadUpdateButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }

        DownloadUpdateButton.Content = string.IsNullOrWhiteSpace(asset.Name)
            ? "Download update"
            : $"Download {asset.Name}";
        DownloadUpdateButton.NavigateUri = new Uri(asset.BrowserDownloadUrl!);
        DownloadUpdateButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void SetReleaseNotes(LatestReleaseDto latest)
    {
        var notes = latest.Body?.Trim();
        if (string.IsNullOrWhiteSpace(notes))
        {
            ReleaseNotesText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }

        if (notes.Length > 1800)
            notes = notes[..1800] + Environment.NewLine + "...";

        ReleaseNotesText.Text = notes;
        ReleaseNotesText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private static bool IsPreferredUpdateAsset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Trim();
        if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            token = token[1..];

        var plusIdx = token.IndexOf('+');
        if (plusIdx >= 0)
            token = token[..plusIdx];

        var dashIdx = token.IndexOf('-');
        if (dashIdx >= 0)
            token = token[..dashIdx];

        if (!Version.TryParse(token, out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    private static string ResolveVersion()
    {
        // Prefer MSIX package version when available, otherwise fall back to assembly version.
        try
        {
            var v = Package.Current.Id.Version;
            if (v.Major != 0 || v.Minor != 0 || v.Build != 0 || v.Revision != 0)
                return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged contexts may not support Package.Current.
        }

        try
        {
            var asm = typeof(AboutPage).GetTypeInfo().Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return SanitizeDisplayVersion(info);

            return asm.GetName().Version?.ToString() ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    private static string SanitizeDisplayVersion(string versionText)
    {
        var token = versionText.Trim();
        if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            token = token[1..];

        var plusIdx = token.IndexOf('+');
        if (plusIdx >= 0)
            token = token[..plusIdx];

        var dashIdx = token.IndexOf('-');
        if (dashIdx >= 0)
            token = token[..dashIdx];

        return string.IsNullOrWhiteSpace(token) ? versionText : token;
    }

    private sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public LatestReleaseAssetDto[]? Assets { get; set; }
    }

    private sealed class LatestReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
