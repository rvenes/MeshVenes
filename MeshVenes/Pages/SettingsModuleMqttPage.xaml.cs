using Meshtastic.Protobufs;
using MeshVenes.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace MeshVenes.Pages;

public sealed partial class SettingsModuleMqttPage : Page
{
    public SettingsModuleMqttPage()
    {
        InitializeComponent();
        Loaded += SettingsModuleMqttPage_Loaded;
        Unloaded += SettingsModuleMqttPage_Unloaded;
    }

    private async void SettingsModuleMqttPage_Loaded(object sender, RoutedEventArgs e)
    {
        MqttProxyService.Instance.StateChanged += MqttProxyService_StateChanged;
        UpdateRuntimeStatusUi();
        await LoadAsync();
    }

    private void SettingsModuleMqttPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MqttProxyService.Instance.StateChanged -= MqttProxyService_StateChanged;
    }

    private void MqttProxyService_StateChanged()
    {
        try
        {
            _ = DispatcherQueue.TryEnqueue(UpdateRuntimeStatusUi);
        }
        catch
        {
            // Ignore late callbacks during page unload.
        }
    }

    private async Task LoadAsync()
    {
        NodeText.Text = "Configuration for: " + NodeIdentity.ConnectedNodeLabel();
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node to edit MQTT settings.";
            return;
        }

        try
        {
            StatusText.Text = "Loading MQTT configuration...";
            var module = await AdminConfigClient.Instance.GetModuleConfigAsync(nodeNum, AdminMessage.Types.ModuleConfigType.MqttConfig);
            var config = module.Mqtt ?? new ModuleConfig.Types.MQTTConfig();
            var map = config.MapReportSettings ?? new ModuleConfig.Types.MapReportSettings();

            EnabledToggle.IsOn = config.Enabled;
            ProxyToggle.IsOn = config.ProxyToClientEnabled;
            EncryptionToggle.IsOn = config.EncryptionEnabled;
            JsonToggle.IsOn = config.JsonEnabled;
            MapReportToggle.IsOn = config.MapReportingEnabled;
            ConsentToggle.IsOn = map.ShouldReportLocation;
            RootBox.Text = config.Root ?? string.Empty;
            AddressBox.Text = config.Address ?? string.Empty;
            UsernameBox.Text = config.Username ?? string.Empty;
            PasswordBox.Text = config.Password ?? string.Empty;
            TlsToggle.IsOn = config.TlsEnabled;

            StatusText.Text = $"Loaded from node 0x{nodeNum:x8}.";
            await MqttProxyService.Instance.RefreshFromConnectedNodeConfigAsync(forceReconnect: false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load MQTT configuration: " + ex.Message;
        }

        UpdateRuntimeStatusUi();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!NodeIdentity.TryGetConnectedNodeNum(out var nodeNum))
        {
            StatusText.Text = "Connect to a node before saving.";
            return;
        }

        try
        {
            var map = new ModuleConfig.Types.MapReportSettings
            {
                ShouldReportLocation = ConsentToggle.IsOn
            };

            var config = new ModuleConfig.Types.MQTTConfig
            {
                Enabled = EnabledToggle.IsOn,
                ProxyToClientEnabled = ProxyToggle.IsOn,
                EncryptionEnabled = EncryptionToggle.IsOn,
                JsonEnabled = JsonToggle.IsOn,
                MapReportingEnabled = MapReportToggle.IsOn,
                MapReportSettings = map,
                Root = (RootBox.Text ?? string.Empty).Trim(),
                Address = (AddressBox.Text ?? string.Empty).Trim(),
                Username = (UsernameBox.Text ?? string.Empty).Trim(),
                Password = PasswordBox.Text ?? string.Empty,
                TlsEnabled = TlsToggle.IsOn
            };

            StatusText.Text = "Saving MQTT configuration...";
            await AdminConfigClient.Instance.SaveModuleConfigAsync(nodeNum, new ModuleConfig { Mqtt = config });
            StatusText.Text = "MQTT configuration saved.";
            await MqttProxyService.Instance.RefreshFromConnectedNodeConfigAsync(forceReconnect: true);
            SettingsReconnectHelper.StartPostSaveReconnectWatchdog(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
        }
        catch (Exception ex)
        {
            if (SettingsReconnectHelper.IsNotConnectedException(ex))
            {
                StatusText.Text = "Node reboot detected. Connecting...";
                var reconnected = await SettingsReconnectHelper.TryReconnectAfterSaveAsync(text => _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text));
                StatusText.Text = reconnected
                    ? "MQTT configuration saved. Reconnected."
                    : "MQTT configuration may be saved, but reconnect failed.";
                if (reconnected)
                    await MqttProxyService.Instance.RefreshFromConnectedNodeConfigAsync(forceReconnect: true);
                return;
            }

            StatusText.Text = "Failed to save MQTT configuration: " + ex.Message;
        }
    }

    private async void ReconnectProxy_Click(object sender, RoutedEventArgs e)
    {
        RuntimeStatusText.Text = "Proxy reconnect requested...";
        await MqttProxyService.Instance.ForceReconnectAsync();
        UpdateRuntimeStatusUi();
    }

    private void UpdateRuntimeStatusUi()
    {
        var snapshot = MqttProxyService.Instance.GetSnapshot();
        RuntimeStatusText.Text = "Status: " + snapshot.RuntimeStatus + (snapshot.IsBrokerConnected ? " (connected)" : "");
        RuntimeBrokerText.Text = "Broker: " + snapshot.Broker + " | Topic filter: " + snapshot.TopicFilter;
        RuntimeCountersText.Text =
            $"Node -> broker: {snapshot.NodeToBrokerCount} | Broker -> node: {snapshot.BrokerToNodeCount} | Dropped: {snapshot.DroppedCount}";
        RuntimeErrorText.Text = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? string.Empty
            : "Last error: " + snapshot.LastError;
    }
}
