using Microsoft.UI.Xaml.Controls;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        WebViewDevToolsToggle.IsOn = AppState.EnableWebViewDevTools;
    }

    private void WebViewDevToolsToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppState.EnableWebViewDevTools = WebViewDevToolsToggle.IsOn;
    }
}
