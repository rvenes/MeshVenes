using MeshVenes.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
    }

    public string VersionText => $"Version: {UpdateService.CurrentVersionText}";

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

    private async void CheckForUpdates_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Update status: checking for updates...";
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckResult result;
        try
        {
            result = await UpdateService.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update status: check failed ({ex.Message}).";
            return;
        }

        UpdateStatusText.Text = result.Message ?? "";
        SetReleaseLink(result.Status == UpdateStatus.UpToDate ? null : result.ReleaseUrl);
        UpdateInstallButton.Visibility =
            result.Status == UpdateStatus.UpdateAvailable && UpdateService.CanSelfUpdate()
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private async void UpdateInstall_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (UpdateService.LastResult?.Update is not { } update)
            return;

        try
        {
            UpdateInstallButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateStatusText.Text = $"Update status: downloading {update.VersionText}...";

            var progress = new Progress<double>(value => UpdateProgressBar.Value = value);
            await UpdateService.DownloadAndInstallAsync(update, progress);
            // On success the app exits and the updater script takes over.
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update status: install failed ({ex.Message}).";
            UpdateProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            UpdateInstallButton.IsEnabled = true;
        }
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

}
