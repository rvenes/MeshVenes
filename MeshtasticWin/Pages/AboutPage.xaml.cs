using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace MeshtasticWin.Pages;

public sealed partial class AboutPage : Page
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/rvenes/MeshtasticWin/releases/latest";
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    public string VersionText { get; }

    public AboutPage()
    {
        InitializeComponent();
        VersionText = $"Version: {ResolveVersion()}";
        Loaded += AboutPage_Loaded;
    }

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
        try
        {
            var localAssetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "paypalqr.png");
            if (File.Exists(localAssetPath))
            {
                PayPalQrImage.Source = new BitmapImage(new Uri(localAssetPath));
                return;
            }
        }
        catch
        {
            // Fall back to packaged asset URI.
        }

        try
        {
            PayPalQrImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/paypalqr.png"));
        }
        catch
        {
            // Keep default empty state if no source can be resolved.
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
                return;
            }

            if (remote > current)
            {
                UpdateStatusText.Text = $"Update status: new version available ({latest.TagName}).";
                SetReleaseLink(latest.HtmlUrl);
            }
            else
            {
                UpdateStatusText.Text = $"Update status: you are up to date ({currentVersion}).";
                UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update status: check failed ({ex.Message}).";
            UpdateLinkButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshtasticWin/1.0 (+https://github.com/rvenes/MeshtasticWin)");
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
                return info;

            return asm.GetName().Version?.ToString() ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    private sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
