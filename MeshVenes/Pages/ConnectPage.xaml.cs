using AppDataPaths = MeshVenes.Services.AppDataPaths;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeshVenes.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage;

namespace MeshVenes.Pages;

public sealed partial class ConnectPage : Page
{
    private const int AutoConnectDelaySeconds = 5;

    // Auto connect runs once per app start, not on every navigation to this page.
    private static bool s_autoConnectHandled;

    private bool _handlersHooked;
    private bool _hasPorts;
    private bool _hasBluetoothDevices;
    private bool _isBluetoothScanning;
    private DispatcherTimer? _autoConnectTimer;
    private int _autoConnectSecondsLeft;

    private readonly ObservableCollection<string> _logView = new();
    private bool _logOptionsReady;

    public ConnectPage()
    {
        InitializeComponent();

        LogList.ItemsSource = _logView;

        TcpHostBox.Text = SettingsStore.GetString(SettingsStore.LastTcpHostKey) ?? "127.0.0.1";
        TcpPortBox.Text = SettingsStore.GetString(SettingsStore.LastTcpPortKey) ?? "4403";
        AutoConnectCheck.IsChecked = SettingsStore.GetString(SettingsStore.AutoConnectLastKey) == "1";
        HideTxCheck.IsChecked = (SettingsStore.GetString(SettingsStore.ConnectLogHideTxKey) ?? "1") == "1";
        ExtendedInfoCheck.IsChecked = SettingsStore.GetString(SettingsStore.ConnectLogExtendedKey) == "1";
        _logOptionsReady = true;

        HookClientEvents();
        RebuildLogView();
        UpdateUiFromClient();
        StartAutoConnectCountdown();
    }

    private void AutoConnectCheck_Checked(object sender, RoutedEventArgs e)
        => SettingsStore.SetString(SettingsStore.AutoConnectLastKey, "1");

    private void AutoConnectCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        SettingsStore.SetString(SettingsStore.AutoConnectLastKey, "0");
        CancelAutoConnect("Auto connect cancelled.");
    }

    private string? _autoConnectTarget;

    private void StartAutoConnectCountdown()
    {
        if (s_autoConnectHandled)
            return;

        s_autoConnectHandled = true;

        if (AutoConnectCheck.IsChecked != true ||
            !RadioClient.Instance.ConnectionState.CanConnect)
            return;

        _autoConnectTarget = DescribeSavedEndpoint();
        if (_autoConnectTarget is null)
            return;

        _autoConnectSecondsLeft = AutoConnectDelaySeconds;
        _autoConnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoConnectTimer.Tick += AutoConnectTimer_Tick;
        _autoConnectTimer.Start();
        StatusText.Text = $"Auto connect to {_autoConnectTarget} in {_autoConnectSecondsLeft}s...";
    }

    private static string? DescribeSavedEndpoint()
    {
        var preferred = (SettingsStore.GetString(SettingsStore.LastConnectionTypeKey) ?? "").Trim().ToLowerInvariant();
        var serialPort = SettingsStore.GetString(SettingsStore.LastSerialPortKey)?.Trim();
        var tcpHost = SettingsStore.GetString(SettingsStore.LastTcpHostKey)?.Trim();
        var tcpPort = SettingsStore.GetString(SettingsStore.LastTcpPortKey)?.Trim();
        var bleId = SettingsStore.GetString(SettingsStore.LastBluetoothDeviceIdKey)?.Trim();

        var tcpTarget = string.IsNullOrWhiteSpace(tcpHost) ? null : $"{tcpHost}:{tcpPort}";

        return preferred switch
        {
            "serial" when !string.IsNullOrWhiteSpace(serialPort) => serialPort,
            "tcp" when tcpTarget is not null => tcpTarget,
            "ble" when !string.IsNullOrWhiteSpace(bleId) => "Bluetooth device",
            _ => !string.IsNullOrWhiteSpace(serialPort)
                ? serialPort
                : tcpTarget ?? (string.IsNullOrWhiteSpace(bleId) ? null : "Bluetooth device")
        };
    }

    private async void AutoConnectTimer_Tick(object? sender, object e)
    {
        if (!RadioClient.Instance.ConnectionState.CanConnect)
        {
            CancelAutoConnect(null);
            return;
        }

        _autoConnectSecondsLeft--;
        if (_autoConnectSecondsLeft > 0)
        {
            StatusText.Text = $"Auto connect to {_autoConnectTarget} in {_autoConnectSecondsLeft}s...";
            return;
        }

        CancelAutoConnect(null);
        StatusText.Text = $"Auto connecting to {_autoConnectTarget}...";
        AddLogLineUi($"Auto connect: trying last used connection ({_autoConnectTarget}).");

        try
        {
            var ok = await SettingsReconnectHelper.TryConnectToSavedEndpointOnceAsync();
            UpdateUiFromClient();
            if (!ok)
                StatusText.Text = "Auto connect failed. Connect manually.";
        }
        catch (OperationCanceledException)
        {
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Auto connect failed: " + ex.Message;
        }
    }

    private void CancelAutoConnect(string? statusText)
    {
        if (_autoConnectTimer is not null)
        {
            _autoConnectTimer.Stop();
            _autoConnectTimer = null;
        }

        if (statusText is not null)
            StatusText.Text = statusText;
    }

    private void HookClientEvents()
    {
        if (_handlersHooked)
            return;

        _handlersHooked = true;
        RadioClient.Instance.ConnectionChanged += OnConnectionChanged;
        RadioClient.Instance.LogLines.CollectionChanged += LogLines_CollectionChanged;
    }

    private void UnhookClientEvents()
    {
        if (!_handlersHooked)
            return;

        _handlersHooked = false;
        RadioClient.Instance.ConnectionChanged -= OnConnectionChanged;
        RadioClient.Instance.LogLines.CollectionChanged -= LogLines_CollectionChanged;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        UnhookClientEvents();
        base.OnNavigatedFrom(e);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        HookClientEvents();
        RebuildLogView();
        RefreshPorts();
        _ = RefreshBluetoothDevicesAsync();
        UpdateUiFromClient();
        base.OnNavigatedTo(e);
    }

    private void OnConnectionChanged()
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is not null)
                _ = dq.TryEnqueue(UpdateUiFromClient);
        }
        catch
        {
            // Page/window may be shutting down.
        }
    }

    private void AddLogLineUi(string line)
    {
        RadioClient.Instance.AddLogFromUiThread(line);
    }

    private void LogToUi(string line)
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is not null)
                _ = dq.TryEnqueue(() => AddLogLineUi(line));
        }
        catch
        {
            // Ignore late callbacks during app shutdown.
        }
    }

    private Action<Action> RunOnUi()
        => a => DispatcherQueue.TryEnqueue(() => a());

    private void UpdateUiFromClient()
    {
        var client = RadioClient.Instance;
        var state = client.ConnectionState;

        StatusText.Text = state.Status switch
        {
            RadioConnectionStatus.Connecting => $"Connecting to {state.Endpoint}...",
            RadioConnectionStatus.Connected => $"Connected to {state.Endpoint}",
            RadioConnectionStatus.Reconnecting => string.IsNullOrWhiteSpace(state.Endpoint)
                ? "Reconnecting..."
                : $"Reconnecting to {state.Endpoint}...",
            RadioConnectionStatus.Disconnecting => "Disconnecting...",
            RadioConnectionStatus.Failed => string.IsNullOrWhiteSpace(state.ErrorMessage)
                ? "Connection failed"
                : $"Connection failed: {state.ErrorMessage}",
            _ => "Disconnected"
        };

        ConnectButton.IsEnabled = state.CanConnect && _hasPorts;
        TcpConnectButton.IsEnabled = state.CanConnect;
        BluetoothConnectButton.IsEnabled = state.CanConnect && _hasBluetoothDevices && !_isBluetoothScanning;
        DisconnectButton.IsEnabled = state.CanDisconnect;

        PortCombo.IsEnabled = state.CanConnect && _hasPorts;
        TcpHostBox.IsEnabled = state.CanConnect;
        TcpPortBox.IsEnabled = state.CanConnect;
        BluetoothCombo.IsEnabled = state.CanConnect && _hasBluetoothDevices && !_isBluetoothScanning;
        RefreshPortsButton.IsEnabled = state.CanConnect;
        RefreshBluetoothButton.IsEnabled = state.CanConnect && !_isBluetoothScanning;
        BluetoothScanRing.IsActive = _isBluetoothScanning;
        BluetoothScanRing.Visibility = _isBluetoothScanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoConnect(null);
        try
        {
            var port = PortCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(port))
            {
                AddLogLineUi("No serial port selected.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            await RadioClient.Instance.ConnectAsync(port, RunOnUi(), LogToUi);

            SettingsStore.SetString(SettingsStore.LastSerialPortKey, port);
            SettingsStore.SetString(SettingsStore.LastConnectionTypeKey, "serial");
            UpdateUiFromClient();
        }
        catch (OperationCanceledException)
        {
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private async void TcpConnect_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoConnect(null);
        try
        {
            var host = TcpHostBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                AddLogLineUi("TCP host is required.");
                UpdateUiFromClient();
                return;
            }

            if (!int.TryParse(TcpPortBox.Text?.Trim(), out var port) || port < 1 || port > 65535)
            {
                AddLogLineUi("TCP port must be 1-65535.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            await RadioClient.Instance.ConnectTcpAsync(host, port, RunOnUi(), LogToUi);

            SettingsStore.SetString(SettingsStore.LastTcpHostKey, host);
            SettingsStore.SetString(SettingsStore.LastTcpPortKey, port.ToString());
            SettingsStore.SetString(SettingsStore.LastConnectionTypeKey, "tcp");
            UpdateUiFromClient();
        }
        catch (OperationCanceledException)
        {
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private async void BluetoothConnect_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoConnect(null);
        try
        {
            if (BluetoothCombo.SelectedItem is not BluetoothDeviceOption option)
            {
                AddLogLineUi("No Bluetooth device selected.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            AddLogLineUi($"Connecting to Bluetooth {option.DisplayName}...");
            await RadioClient.Instance.ConnectBluetoothAsync(option.DeviceId, option.DisplayName, RunOnUi(), LogToUi, PromptBluetoothPinAsync);

            SettingsStore.SetString(SettingsStore.LastBluetoothDeviceIdKey, option.DeviceId);
            SettingsStore.SetString(SettingsStore.LastConnectionTypeKey, "ble");
            UpdateUiFromClient();
        }
        catch (OperationCanceledException)
        {
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi($"Bluetooth connect failed: {ex.Message}");
            UpdateUiFromClient();
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        UpdateUiFromClient();
    }

    private async void RefreshBluetooth_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBluetoothDevicesAsync();
        UpdateUiFromClient();
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortCombo.SelectedItem is string port)
            SettingsStore.SetString(SettingsStore.LastSerialPortKey, port);
    }

    private void BluetoothCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BluetoothCombo.SelectedItem is BluetoothDeviceOption option)
            SettingsStore.SetString(SettingsStore.LastBluetoothDeviceIdKey, option.DeviceId);
    }

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames();
        var sorted = ports
            .OrderBy(GetPortSortKey)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PortCombo.Items.Clear();

        if (sorted.Count == 0)
        {
            var emptyItem = new ComboBoxItem
            {
                Content = "(no ports found)",
                IsEnabled = false
            };

            PortCombo.Items.Add(emptyItem);
            PortCombo.SelectedIndex = 0;
            PortCombo.IsEnabled = false;
            _hasPorts = false;
            return;
        }

        foreach (var port in sorted)
            PortCombo.Items.Add(port);

        PortCombo.IsEnabled = true;
        _hasPorts = true;

        var savedPort = SettingsStore.GetString(SettingsStore.LastSerialPortKey);
        var initialPort = !string.IsNullOrWhiteSpace(savedPort) && sorted.Contains(savedPort, StringComparer.OrdinalIgnoreCase)
            ? sorted.First(p => string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase))
            : sorted[0];

        PortCombo.SelectedItem = initialPort;
    }

    private async Task RefreshBluetoothDevicesAsync()
    {
        try
        {
            _isBluetoothScanning = true;
            BluetoothCombo.Items.Clear();
            BluetoothCombo.Items.Add(new ComboBoxItem
            {
                Content = "(scanning...)",
                IsEnabled = false
            });
            BluetoothCombo.SelectedIndex = 0;
            BluetoothCombo.IsEnabled = false;
            _hasBluetoothDevices = false;
            UpdateUiFromClient();

            var options = (await ScanBluetoothDevicesAsync())
                .OrderBy(d => GetBluetoothSortKey(d.DisplayName))
                .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.Count == 0)
            {
                BluetoothCombo.Items.Add(new ComboBoxItem
                {
                    Content = "(no bluetooth devices found)",
                    IsEnabled = false
                });
                BluetoothCombo.SelectedIndex = 0;
                BluetoothCombo.IsEnabled = false;
                _hasBluetoothDevices = false;
                UpdateUiFromClient();
                return;
            }

            foreach (var option in options)
                BluetoothCombo.Items.Add(option);

            BluetoothCombo.IsEnabled = true;
            _hasBluetoothDevices = true;

            var savedDeviceId = SettingsStore.GetString(SettingsStore.LastBluetoothDeviceIdKey);
            var initial = !string.IsNullOrWhiteSpace(savedDeviceId)
                ? options.FirstOrDefault(o => string.Equals(o.DeviceId, savedDeviceId, StringComparison.OrdinalIgnoreCase)) ?? options[0]
                : options[0];

            BluetoothCombo.SelectedItem = initial;
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            BluetoothCombo.Items.Clear();
            BluetoothCombo.Items.Add(new ComboBoxItem
            {
                Content = "(bluetooth unavailable)",
                IsEnabled = false
            });
            BluetoothCombo.SelectedIndex = 0;
            BluetoothCombo.IsEnabled = false;
            _hasBluetoothDevices = false;
            AddLogLineUi($"Bluetooth scan failed: {ex.Message}");
            UpdateUiFromClient();
        }
        finally
        {
            _isBluetoothScanning = false;
            UpdateUiFromClient();
        }
    }

    private static async Task<IReadOnlyList<BluetoothDeviceOption>> ScanBluetoothDevicesAsync()
    {
        // Active advertisement scan finds Meshtastic devices that are powered
        // on and in range right now, including never-paired ones. The paired
        // list adds devices Windows knows that may be asleep or out of range.
        var advertisingTask = ScanAdvertisingMeshtasticAsync(TimeSpan.FromSeconds(6));
        var pairedTask = FindPairedBleDevicesAsync(TimeSpan.FromSeconds(4));

        var advertising = await advertisingTask;
        var paired = await pairedTask;

        var options = new List<BluetoothDeviceOption>();
        var matchedAddresses = new HashSet<ulong>();

        foreach (var device in paired)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
                continue;

            var address = TryParseAddressFromDeviceId(device.Id);
            var inRange = address.HasValue && advertising.ContainsKey(address.Value);
            if (inRange)
                matchedAddresses.Add(address!.Value);

            var name = !string.IsNullOrWhiteSpace(device.Name)
                ? device.Name.Trim()
                : inRange && !string.IsNullOrWhiteSpace(advertising[address!.Value])
                    ? advertising[address!.Value].Trim()
                    : "(Unnamed device)";

            options.Add(new BluetoothDeviceOption(
                device.Id,
                inRange ? $"{name} [paired, in range]" : $"{name} [paired]"));
        }

        foreach (var (address, advertisedName) in advertising)
        {
            if (matchedAddresses.Contains(address))
                continue;

            var name = string.IsNullOrWhiteSpace(advertisedName)
                ? FormatBluetoothAddress(address)
                : advertisedName.Trim();

            options.Add(new BluetoothDeviceOption(
                $"{BluetoothLeTransport.AddressIdPrefix}{address:X12}",
                $"{name} [in range]"));
        }

        return options;
    }

    private static async Task<IReadOnlyDictionary<ulong, string>> ScanAdvertisingMeshtasticAsync(TimeSpan duration)
    {
        // Do NOT filter the watcher on the Meshtastic service UUID: the device
        // name ("Meshtastic_XXXX") arrives in a scan response packet that does
        // not repeat the service UUID, so a UUID filter would drop exactly the
        // packets carrying the name. Listen to everything and correlate per
        // address instead.
        var names = new System.Collections.Concurrent.ConcurrentDictionary<ulong, string>();
        var meshtasticAddresses = new System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var advertisement = args.Advertisement;
            if (advertisement is null)
                return;

            if (!string.IsNullOrWhiteSpace(advertisement.LocalName))
                names[args.BluetoothAddress] = advertisement.LocalName;

            try
            {
                if (advertisement.ServiceUuids.Contains(BluetoothLeTransport.MeshtasticServiceUuid))
                    meshtasticAddresses[args.BluetoothAddress] = 1;
            }
            catch (ArgumentException) { }
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            await Task.Delay(duration);
        }
        finally
        {
            try { watcher.Stop(); } catch { }
            watcher.Received -= OnReceived;
        }

        var found = new Dictionary<ulong, string>();
        foreach (var address in meshtasticAddresses.Keys)
            found[address] = names.TryGetValue(address, out var name) ? name : "";

        // Fallback for devices whose scan response was not captured: ask
        // Windows for the cached GAP device name.
        foreach (var address in found.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList())
        {
            try
            {
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (!string.IsNullOrWhiteSpace(device?.Name))
                    found[address] = device.Name;
            }
            catch { }
        }

        return found;
    }

    private static async Task<IReadOnlyList<DeviceInformation>> FindPairedBleDevicesAsync(TimeSpan timeout)
    {
        try
        {
            var queryTask = DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask();
            var completedTask = await Task.WhenAny(queryTask, Task.Delay(timeout));
            if (completedTask != queryTask)
            {
                // Consume potential late exceptions from the timed out query.
                _ = queryTask.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return Array.Empty<DeviceInformation>();
            }

            return (await queryTask).ToList();
        }
        catch
        {
            return Array.Empty<DeviceInformation>();
        }
    }

    private static ulong? TryParseAddressFromDeviceId(string deviceId)
    {
        // Windows BLE device ids end with the device MAC: "...-aa:bb:cc:dd:ee:ff".
        var dash = deviceId.LastIndexOf('-');
        if (dash < 0 || dash >= deviceId.Length - 1)
            return null;

        var hex = deviceId[(dash + 1)..].Replace(":", "");
        return hex.Length == 12 &&
               ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
            ? address
            : null;
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static (int group, string name) GetBluetoothSortKey(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) &&
            displayName.IndexOf("meshtastic", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return (0, displayName);
        }

        return (1, displayName ?? "");
    }

    private static (int group, int number, string name) GetPortSortKey(string port)
    {
        if (!string.IsNullOrWhiteSpace(port) &&
            port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(port[3..], out var number))
        {
            return (0, number, port);
        }

        return (1, int.MaxValue, port ?? "");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoConnect(null);
        SettingsReconnectHelper.CancelPendingConnectionAttempts();
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

    private void SaveDebugLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var debugDir = Path.Combine(AppDataPaths.DebugLogsRootPath, AppDataPaths.ActiveNodeScope);
            Directory.CreateDirectory(debugDir);

            var fileName = $"connect_debug_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            var path = Path.Combine(debugDir, fileName);

            var snapshot = RadioClient.Instance.LogLines.ToArray();
            File.WriteAllLines(path, snapshot);

            AddLogLineUi($"Debug log saved: {path}");
        }
        catch (Exception ex)
        {
            AddLogLineUi($"Failed to save debug log: {ex.Message}");
        }
    }

    private void LogViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_logOptionsReady)
            return;

        SettingsStore.SetString(SettingsStore.ConnectLogHideTxKey, HideTxCheck.IsChecked == true ? "1" : "0");
        SettingsStore.SetString(SettingsStore.ConnectLogExtendedKey, ExtendedInfoCheck.IsChecked == true ? "1" : "0");
        RebuildLogView();
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && e.NewItems is not null)
        {
            for (var i = e.NewItems.Count - 1; i >= 0; i--)
            {
                if (e.NewItems[i] is string raw && TryPresentLogLine(raw, out var line))
                {
                    _logView.Insert(0, line);
                    while (_logView.Count > 500)
                        _logView.RemoveAt(_logView.Count - 1);
                }
            }
            return;
        }

        // The source only removes tail lines to enforce its cap; the view keeps
        // its own cap, so only structural changes need a full rebuild.
        if (e.Action == NotifyCollectionChangedAction.Remove)
            return;

        RebuildLogView();
    }

    private void RebuildLogView()
    {
        _logView.Clear();
        foreach (var raw in RadioClient.Instance.LogLines)
        {
            if (TryPresentLogLine(raw, out var line))
                _logView.Add(line);
        }
    }

    private bool TryPresentLogLine(string raw, out string presented)
    {
        presented = raw;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Lines are timestamped at the source ("20:25:01 ..."); older lines may
        // not be, so treat the stamp as optional.
        var stampMatch = Regex.Match(raw, @"^(\d{2}:\d{2}:\d{2}) (.*)$", RegexOptions.Singleline);
        var stamp = stampMatch.Success ? stampMatch.Groups[1].Value : null;
        var body = stampMatch.Success ? stampMatch.Groups[2].Value : raw;

        if (HideTxCheck.IsChecked == true && Regex.IsMatch(body, @"^TX \d+ bytes$"))
            return false;

        var text = ExtendedInfoCheck.IsChecked == true ? RewriteExtended(body) : HumanizeUptimeInline(body);
        presented = stamp is null ? text : $"{stamp} {text}";
        return true;
    }

    private static string RewriteExtended(string body)
    {
        var telemetry = Regex.Match(body, @"^Telemetry: (\w+): (\{.*\})$");
        if (telemetry.Success)
            return FormatTelemetry(telemetry.Groups[1].Value, telemetry.Groups[2].Value) ?? HumanizeUptimeInline(body);

        var gps = Regex.Match(body, @"^GPS: ([0-9A-Fa-f]{4}) (-?[\d.]+),(-?[\d.]+) \((.*)\) src=(\S+)$");
        if (gps.Success)
        {
            var shortId = gps.Groups[1].Value;
            var node = ResolveNodeByShortId(shortId);
            var who = string.IsNullOrWhiteSpace(node?.Name) ? shortId : $"{node!.Name} ({shortId})";
            var lat = gps.Groups[2].Value;
            var lon = gps.Groups[3].Value;
            var position = lat.StartsWith("0.000000") && lon.StartsWith("0.000000") ? "no fix" : $"{lat}, {lon}";
            return $"Position from {who}: {position} at {gps.Groups[4].Value} (source: {gps.Groups[5].Value})";
        }

        var ack = Regex.Match(body, @"^ACK \(unknown request_id=(0x[0-9a-fA-F]+)\)$");
        if (ack.Success)
            return $"ACK heard for a packet not sent by this app (id {ack.Groups[1].Value})";

        if (body.StartsWith("TXT: ", StringComparison.Ordinal))
            return "Device log: " + body[5..];

        return HumanizeUptimeInline(body);
    }

    private static string? FormatTelemetry(string kind, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                parts.Add(FormatTelemetryValue(prop));

            return parts.Count == 0 ? null : $"Telemetry ({kind}): {string.Join(", ", parts)}";
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTelemetryValue(JsonProperty prop)
    {
        var value = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetDouble() : double.NaN;
        return prop.Name switch
        {
            // Firmware reports > 100 when running on external power.
            "batteryLevel" => value > 100 ? "external power" : $"battery {value:0} %",
            "voltage" => string.Create(CultureInfo.InvariantCulture, $"{value:0.00} V"),
            "channelUtilization" => string.Create(CultureInfo.InvariantCulture, $"channel util {value:0.0} %"),
            "airUtilTx" => string.Create(CultureInfo.InvariantCulture, $"air TX {value:0.00} %"),
            "uptimeSeconds" => $"uptime {HumanizeSeconds((long)value)}",
            "temperature" => string.Create(CultureInfo.InvariantCulture, $"temperature {value:0.0} °C"),
            "relativeHumidity" => string.Create(CultureInfo.InvariantCulture, $"humidity {value:0} %"),
            "barometricPressure" => string.Create(CultureInfo.InvariantCulture, $"pressure {value:0} hPa"),
            _ => prop.Value.ValueKind == JsonValueKind.Number
                ? string.Create(CultureInfo.InvariantCulture, $"{HumanizeCamelCase(prop.Name)} {value:0.##}")
                : $"{HumanizeCamelCase(prop.Name)} {prop.Value}"
        };
    }

    private static string HumanizeCamelCase(string name)
    {
        var text = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return text.ToLowerInvariant();
    }

    private static string HumanizeUptimeInline(string body)
        => Regex.Replace(body, "\"uptimeSeconds\": (\\d+)", m =>
            $"\"uptimeSeconds\": {m.Groups[1].Value} ({HumanizeSeconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))})");

    private static string HumanizeSeconds(long seconds)
    {
        if (seconds < 0)
            return $"{seconds} s";

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{seconds} s";
    }

    private static MeshVenes.Models.NodeLive? ResolveNodeByShortId(string shortId)
        => MeshVenes.AppState.Nodes.FirstOrDefault(n =>
            !string.IsNullOrWhiteSpace(n.IdHex) &&
            n.IdHex.EndsWith(shortId, StringComparison.OrdinalIgnoreCase));

    private void LogList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not string line)
            return;

        var node = ResolveNodeFromLogLine(line);
        if (node is null || string.IsNullOrWhiteSpace(node.IdHex))
            return;

        // Same mechanism as "Open node" on the Messages page.
        SettingsStore.SetString("NodesLastSelectedNodeIdHex", node.IdHex);
        App.MainWindowInstance?.NavigateTo("nodes");
    }

    private static MeshVenes.Models.NodeLive? ResolveNodeFromLogLine(string line)
    {
        var full = Regex.Match(line, @"0x[0-9a-fA-F]{8}");
        if (full.Success)
        {
            var byFullId = MeshVenes.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, full.Value, StringComparison.OrdinalIgnoreCase));
            if (byFullId is not null)
                return byFullId;
        }

        foreach (Match m in Regex.Matches(line, @"\b[0-9a-fA-F]{4}\b"))
        {
            // Timestamps/uptime values are digit-only tokens too, so prefer
            // tokens containing hex letters and only then fall back to digits.
            if (!m.Value.Any(char.IsLetter))
                continue;

            var node = ResolveNodeByShortId(m.Value);
            if (node is not null)
                return node;
        }

        foreach (Match m in Regex.Matches(line, @"\b[0-9]{4}\b"))
        {
            var node = ResolveNodeByShortId(m.Value);
            if (node is not null)
                return node;
        }

        return null;
    }

    private void LogList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is string line)
            LogList.SelectedItem = line;
    }

    private void LogListFlyout_Opening(object sender, object e)
    {
        CopyLogMenuItem.IsEnabled = LogList.SelectedItem is string;
    }

    private void CopyLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not string line || string.IsNullOrWhiteSpace(line))
            return;

        CopyTextToClipboard(line);
    }

    private void CopyAllLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var lines = RadioClient.Instance.LogLines;
        if (lines.Count == 0)
            return;

        CopyTextToClipboard(string.Join(Environment.NewLine, lines));
    }

    private static void CopyTextToClipboard(string text)
    {
        _ = ClipboardUtil.TrySetText(text, flush: true);
    }

    private sealed record BluetoothDeviceOption(string DeviceId, string DisplayName);

    /// <summary>
    /// Asks the user for the Bluetooth pairing PIN. Called from a background
    /// thread during pairing, so the dialog is marshalled to the UI thread.
    /// Returns null when the user cancels.
    /// </summary>
    private Task<string?> PromptBluetoothPinAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var pinBox = new TextBox
                {
                    PlaceholderText = "123456",
                    MaxLength = 6
                };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = "Enter the pairing PIN shown on the device screen. Devices without a screen use 123456.",
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(pinBox);

                var dialog = new ContentDialog
                {
                    Title = "Bluetooth pairing",
                    Content = panel,
                    PrimaryButtonText = "Pair",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary
                    ? (string.IsNullOrWhiteSpace(pinBox.Text) ? "123456" : pinBox.Text.Trim())
                    : null);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });

        if (!enqueued)
            tcs.TrySetResult(null);

        return tcs.Task;
    }
}
