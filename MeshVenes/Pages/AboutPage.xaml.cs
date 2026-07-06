using MeshVenes.Services;
using Microsoft.UI.Xaml.Controls;
using System;
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
        await CheckForUpdatesAsync();
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

    private bool _updateStaged;

    private async void UpdateInstall_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_updateStaged)
        {
            // Second click ("Restart now"): close the app so the updater
            // script can apply the staged update and restart.
            UpdateService.RestartToApplyUpdate();
            return;
        }

        if (UpdateService.LastResult?.Update is not { } update)
            return;

        try
        {
            UpdateInstallButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            UpdateStatusText.Text = $"Update status: downloading {update.VersionText}...";

            var progress = new Progress<double>(value => UpdateProgressBar.Value = value);
            await UpdateService.DownloadAndStageAsync(update, progress);

            _updateStaged = true;
            UpdateProgressBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            UpdateStatusText.Text = $"Update status: {update.VersionText} downloaded. " +
                "Restart the app for the update to take effect (it is also applied automatically the next time you close the app).";
            UpdateInstallButton.Content = "Restart now";
            UpdateInstallButton.IsEnabled = true;
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
