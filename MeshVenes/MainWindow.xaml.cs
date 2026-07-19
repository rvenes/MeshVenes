using MeshVenes.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using WinRT.Interop;

namespace MeshVenes;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 1320;
    private const int MinimumWindowHeight = 860;
    private const int InitialWindowWidth = 1440;
    private const int InitialWindowHeight = 900;

    private AppWindow? _appWindow;
    private bool _enforcingMinimumWindowSize;

    public MainWindow()
    {
        InitializeComponent();
        Closed += MainWindow_Closed;
        InitializeWindowSizing();
        ApplyWindowIcon();
        SetInitialWindowSize(InitialWindowWidth, InitialWindowHeight);
        RadioClient.Instance.ConnectionChanged += OnConnectionChanged;
        AppState.ConnectedNodeChanged += OnConnectionChanged;
        AppState.SettingsChanged += OnAppSettingsChanged;
        AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        foreach (var node in AppState.Nodes)
            node.PropertyChanged += Node_PropertyChanged;
        EnqueueConnectionStatusUpdate();
        ApplyAppThemeFromSettings();
        NavigateTo("connect");
        RootGrid.Loaded += (_, __) => _ = RunStartupUpdateCheckAsync();
    }

    private static bool s_startupUpdateCheckDone;

    private async System.Threading.Tasks.Task RunStartupUpdateCheckAsync()
    {
        if (s_startupUpdateCheckDone)
            return;
        s_startupUpdateCheckDone = true;

        if (Packaging.IsPackaged())
            return;

        try
        {
            // Let the app finish starting before touching the network.
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(3));

            var result = await UpdateService.CheckForUpdateAsync();
            if (result.Status != UpdateStatus.UpdateAvailable || result.Update is not { } update)
                return;

            var body = $"MeshVenes {update.VersionText} is available (you have {UpdateService.CurrentVersionText}).";
            if (!string.IsNullOrWhiteSpace(update.Notes))
                body += $"\n\n{update.Notes}";

            var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            var bodyText = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel();
            content.Children.Add(bodyText);
            content.Children.Add(progressBar);

            var dialog = new ContentDialog
            {
                Title = "Update available",
                Content = content,
                PrimaryButtonText = "Update now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };

            var updateStaged = false;
            dialog.PrimaryButtonClick += async (d, args) =>
            {
                if (updateStaged)
                {
                    // Second click ("Restart now"): close the app so the
                    // updater script can apply the update and restart.
                    UpdateService.RestartToApplyUpdate();
                    return;
                }

                var deferral = args.GetDeferral();
                try
                {
                    args.Cancel = true;
                    d.IsPrimaryButtonEnabled = false;
                    d.CloseButtonText = "";
                    bodyText.Text = $"Downloading MeshVenes {update.VersionText}...";
                    progressBar.Visibility = Visibility.Visible;

                    var progress = new Progress<double>(value => progressBar.Value = value);
                    await UpdateService.DownloadAndStageAsync(update, progress);

                    updateStaged = true;
                    progressBar.Visibility = Visibility.Collapsed;
                    bodyText.Text = $"MeshVenes {update.VersionText} has been downloaded. " +
                        "The app must be restarted for the update to take effect. " +
                        "If you choose Later, the update is applied automatically the next time you close the app.";
                    d.PrimaryButtonText = "Restart now";
                    d.IsPrimaryButtonEnabled = true;
                    d.CloseButtonText = "Later";
                }
                catch (Exception ex)
                {
                    bodyText.Text = $"Update failed: {ex.Message}\n\nYou can update manually from the About page or GitHub.";
                    progressBar.Visibility = Visibility.Collapsed;
                    d.IsPrimaryButtonEnabled = true;
                    d.CloseButtonText = "Close";
                }
                finally
                {
                    deferral.Complete();
                }
            };

            _ = await dialog.ShowAsync();
        }
        catch
        {
            // Startup update check must never disturb normal use.
        }
    }

    public void NavigateTo(string tag)
    {
        // Select the menu item that matches the tag.
        foreach (var mi in Nav.MenuItems)
        {
            if (mi is NavigationViewItem nvi && (nvi.Tag?.ToString() == tag))
            {
                Nav.SelectedItem = nvi;
                return;
            }
        }
    }

    private void SetInitialWindowSize(int width, int height)
    {
        if (_appWindow is null)
            return;

        var targetWidth = Math.Max(width, MinimumWindowWidth);
        var targetHeight = Math.Max(height, MinimumWindowHeight);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
    }

    private void InitializeWindowSizing()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Changed += AppWindow_Changed;
    }

    private void ApplyWindowIcon()
    {
        if (_appWindow is null)
            return;

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Ignore icon set failures and keep app startup stable.
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _enforcingMinimumWindowSize)
            return;

        var size = sender.Size;
        var targetWidth = Math.Max(size.Width, MinimumWindowWidth);
        var targetHeight = Math.Max(size.Height, MinimumWindowHeight);

        if (targetWidth == size.Width && targetHeight == size.Height)
            return;

        try
        {
            _enforcingMinimumWindowSize = true;
            sender.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
        }
        finally
        {
            _enforcingMinimumWindowSize = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
            return;

        switch (item.Tag?.ToString())
        {
            case "messages":
                ContentFrame.Navigate(typeof(Pages.MessagesPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "connect":
                ContentFrame.Navigate(typeof(Pages.ConnectPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "nodes":
                ContentFrame.Navigate(typeof(Pages.NodesPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "maps":
                ContentFrame.Navigate(typeof(Pages.MapsPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "settings":
                ContentFrame.Navigate(typeof(Pages.SettingsPage), null, new SuppressNavigationTransitionInfo());
                break;
            case "about":
                ContentFrame.Navigate(typeof(Pages.AboutPage), null, new SuppressNavigationTransitionInfo());
                break;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_appWindow is not null)
            _appWindow.Changed -= AppWindow_Changed;

        RadioClient.Instance.ConnectionChanged -= OnConnectionChanged;
        AppState.ConnectedNodeChanged -= OnConnectionChanged;
        AppState.SettingsChanged -= OnAppSettingsChanged;
        AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        foreach (var node in AppState.Nodes)
            node.PropertyChanged -= Node_PropertyChanged;
        SettingsReconnectHelper.RequestShutdown();
        try { await MqttProxyService.Instance.ShutdownAsync(); }
        catch { }
        try { await RadioClient.Instance.DisconnectAsync(); }
        catch { }
    }

    private void OnConnectionChanged()
        => EnqueueConnectionStatusUpdate();

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is Models.NodeLive node)
                    node.PropertyChanged -= Node_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is Models.NodeLive node)
                    node.PropertyChanged += Node_PropertyChanged;
            }
        }

        EnqueueConnectionStatusUpdate();
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Models.NodeLive node)
            return;

        if (!string.Equals(node.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase))
            return;

        EnqueueConnectionStatusUpdate();
    }

    private void EnqueueConnectionStatusUpdate()
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is null)
                return;

            if (dq.HasThreadAccess)
            {
                UpdateConnectionStatusText();
                return;
            }

            _ = dq.TryEnqueue(UpdateConnectionStatusText);
        }
        catch
        {
            // Ignore late/invalid callbacks during reconnect or shutdown.
        }
    }

    private void UpdateConnectionStatusText()
    {
        var dq = DispatcherQueue;
        if (dq is not null && !dq.HasThreadAccess)
        {
            _ = dq.TryEnqueue(UpdateConnectionStatusText);
            return;
        }

        var connectionState = RadioClient.Instance.ConnectionState;
        if (connectionState.Status != RadioConnectionStatus.Connected)
        {
            ConnectionStatusText.Text = connectionState.Status switch
            {
                RadioConnectionStatus.Connecting => "Connecting...",
                RadioConnectionStatus.Reconnecting => "Reconnecting...",
                RadioConnectionStatus.Disconnecting => "Disconnecting...",
                RadioConnectionStatus.Failed => "Connection failed",
                _ => "Not connected"
            };
            return;
        }

        var label = "";
        if (RadioClient.Instance.IsConnected && !string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
        {
            var node = AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));

            if (node is not null)
            {
                var longName = !string.IsNullOrWhiteSpace(node.LongName)
                    ? node.LongName
                    : !string.IsNullOrWhiteSpace(node.Name)
                        ? node.Name
                        : node.IdHex ?? "";
                var shortName = !string.IsNullOrWhiteSpace(node.ShortName)
                    ? node.ShortName
                    : node.ShortId;

                label = longName;
                if (!string.IsNullOrWhiteSpace(shortName))
                    label += $" ({shortName})";
            }
        }

        ConnectionStatusText.Text = string.IsNullOrWhiteSpace(label)
            ? string.IsNullOrWhiteSpace(connectionState.Endpoint)
                ? "Connected"
                : $"Connected to: {connectionState.Endpoint}"
            : $"Connected to: {label}";
    }

    private void OnAppSettingsChanged()
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is null)
                return;

            if (dq.HasThreadAccess)
            {
                ApplyAppThemeFromSettings();
                return;
            }

            _ = dq.TryEnqueue(ApplyAppThemeFromSettings);
        }
        catch
        {
            // Ignore theme refresh errors.
        }
    }

    private void ApplyAppThemeFromSettings()
    {
        var mode = AppState.AppThemeMode;
        var theme = mode switch
        {
            AppState.ThemeMode.Light => ElementTheme.Light,
            AppState.ThemeMode.Dark => ElementTheme.Dark,
            AppState.ThemeMode.DarkGray => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        RootGrid.RequestedTheme = theme;
        Nav.RequestedTheme = theme;
        ContentFrame.RequestedTheme = theme;
        ClearNavigationThemeOverrides();

        if (mode == AppState.ThemeMode.Light)
        {
            var white = new SolidColorBrush(Colors.White);
            RootGrid.Background = white;
            ContentFrame.Background = white;
            ConnectionStatusText.ClearValue(TextBlock.ForegroundProperty);
        }
        else if (mode == AppState.ThemeMode.Dark)
        {
            var black = new SolidColorBrush(Colors.Black);
            RootGrid.Background = black;
            ContentFrame.Background = black;
            ConnectionStatusText.ClearValue(TextBlock.ForegroundProperty);
        }
        else if (mode == AppState.ThemeMode.DarkGray)
        {
            var contentGray = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 27, 29, 34)); // #1B1D22
            var topPaneGray = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 15, 18)); // #0E0F12
            var topText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 236, 236));  // #ECECEC
            var disabledText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 140, 140, 140));

            RootGrid.Background = contentGray;
            ContentFrame.Background = contentGray;

            Nav.Resources["ApplicationPageBackgroundThemeBrush"] = contentGray;
            Nav.Resources["NavigationViewContentBackground"] = contentGray;
            Nav.Resources["NavigationViewTopPaneBackground"] = topPaneGray;
            Nav.Resources["TopNavigationViewItemForeground"] = topText;
            Nav.Resources["TopNavigationViewItemForegroundPointerOver"] = topText;
            Nav.Resources["TopNavigationViewItemForegroundPressed"] = topText;
            Nav.Resources["TopNavigationViewItemForegroundSelected"] = topText;
            Nav.Resources["TopNavigationViewItemForegroundDisabled"] = disabledText;
            ConnectionStatusText.Foreground = topText;
        }
        else
        {
            RootGrid.ClearValue(Grid.BackgroundProperty);
            ContentFrame.ClearValue(Control.BackgroundProperty);
            ConnectionStatusText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void ClearNavigationThemeOverrides()
    {
        var keys = new[]
        {
            "ApplicationPageBackgroundThemeBrush",
            "NavigationViewContentBackground",
            "NavigationViewTopPaneBackground",
            "TopNavigationViewItemForeground",
            "TopNavigationViewItemForegroundPointerOver",
            "TopNavigationViewItemForegroundPressed",
            "TopNavigationViewItemForegroundSelected",
            "TopNavigationViewItemForegroundDisabled"
        };

        foreach (var key in keys)
        {
            if (Nav.Resources.ContainsKey(key))
                Nav.Resources.Remove(key);
        }
    }
}
