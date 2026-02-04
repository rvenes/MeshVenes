using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeshtasticWin.Services;
using System;

namespace MeshtasticWin.Pages;

public sealed partial class ConnectPage : Page
{
    private bool _handlersHooked;

    public ConnectPage()
    {
        InitializeComponent();

        // Bind loggliste som lever i RadioClient (overlever fane-byting)
        LogList.ItemsSource = RadioClient.Instance.LogLines;

        HookClientEvents();
        UpdateUiFromClient();
    }

    private void HookClientEvents()
    {
        if (_handlersHooked)
            return;

        _handlersHooked = true;
        RadioClient.Instance.ConnectionChanged += OnConnectionChanged;
    }

    private void UnhookClientEvents()
    {
        if (!_handlersHooked)
            return;

        _handlersHooked = false;
        RadioClient.Instance.ConnectionChanged -= OnConnectionChanged;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        UnhookClientEvents();
        base.OnNavigatedFrom(e);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        HookClientEvents();
        UpdateUiFromClient();
        base.OnNavigatedTo(e);
    }

    private void OnConnectionChanged()
    {
        _ = DispatcherQueue.TryEnqueue(UpdateUiFromClient);
    }

    private void AddLogLineUi(string line)
    {
        // Oppdater ObservableCollection på UI-tråd
        RadioClient.Instance.AddLogFromUiThread(line);
    }

    private void LogToUi(string line)
    {
        // Kallast frå transport-trådar → må via Dispatcher.
        _ = DispatcherQueue.TryEnqueue(() => AddLogLineUi(line));
    }

    private void UpdateUiFromClient()
    {
        var client = RadioClient.Instance;

        StatusText.Text = client.IsConnected
            ? $"Connected to {client.PortName}"
            : "Disconnected";

        ConnectButton.IsEnabled = !client.IsConnected;
        DisconnectButton.IsEnabled = client.IsConnected;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var port = PortBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(port))
                port = "COM5";

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            await RadioClient.Instance.ConnectAsync(
                port,
                a => DispatcherQueue.TryEnqueue(() => a()),
                LogToUi);

            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RadioClient.Instance.DisconnectAsync();
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }
}
