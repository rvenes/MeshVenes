using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsAppOptionsPage : Page
{
    public SettingsAppOptionsPage()
    {
        InitializeComponent();
        ShowPowerMetricsToggle.IsOn = AppState.ShowPowerMetricsTab;
        ShowDetectionSensorToggle.IsOn = AppState.ShowDetectionSensorLogTab;
    }

    private void ShowPowerMetricsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppState.ShowPowerMetricsTab = ShowPowerMetricsToggle.IsOn;
    }

    private void ShowDetectionSensorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppState.ShowDetectionSensorLogTab = ShowDetectionSensorToggle.IsOn;
    }
}
