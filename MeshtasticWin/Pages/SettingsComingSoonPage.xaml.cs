using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MeshtasticWin.Pages;

public sealed partial class SettingsComingSoonPage : Page
{
    public SettingsComingSoonPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tag && !string.IsNullOrWhiteSpace(tag))
            TitleText.Text = tag switch
            {
                "device" => "Device Configuration",
                "module" => "Module Configuration",
                "firmware" => "Firmware",
                _ => "Coming soon"
            };
    }
}
