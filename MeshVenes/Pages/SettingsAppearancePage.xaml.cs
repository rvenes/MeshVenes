using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshVenes.Pages;

public sealed partial class SettingsAppearancePage : Page
{
    private bool _initialized;

    public SettingsAppearancePage()
    {
        InitializeComponent();
        Loaded += SettingsAppearancePage_Loaded;
    }

    private void SettingsAppearancePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        ThemeCombo.SelectedIndex = AppState.AppThemeMode switch
        {
            AppState.ThemeMode.Light => 1,
            AppState.ThemeMode.Dark => 2,
            AppState.ThemeMode.DarkGray => 3,
            _ => 0
        };
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
            return;

        AppState.AppThemeMode = ThemeCombo.SelectedIndex switch
        {
            1 => AppState.ThemeMode.Light,
            2 => AppState.ThemeMode.Dark,
            3 => AppState.ThemeMode.DarkGray,
            _ => AppState.ThemeMode.System
        };
    }
}
