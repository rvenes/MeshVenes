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

        if (AutoConnectCheck.IsChecked != true || RadioClient.Instance.IsConnected)
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
        if (RadioClient.Instance.IsConnected)
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

        StatusText.Text = client.IsConnected
            ? $"Connected to {client.PortName}"
            : "Disconnected";

        ConnectButton.IsEnabled = !client.IsConnected && _hasPorts;
        TcpConnectButton.IsEnabled = !client.IsConnected;
        BluetoothConnectButton.IsEnabled = !client.IsConnected && _hasBluetoothDevices && !_isBluetoothScanning;
        DisconnectButton.IsEnabled = client.IsConnected;

        RefreshBluetoothButton.IsEnabled = !_isBluetoothScanning;
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
            await RadioClient.Instance.ConnectBluetoothAsync(option.DeviceId, option.DisplayName, RunOnUi(), LogToUi);

            SettingsStore.SetString(SettingsStore.LastBluetoothDeviceIdKey, option.DeviceId);
            SettingsStore.SetString(SettingsStore.LastConnectionTypeKey, "ble");
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

            var devices = await ScanBluetoothDevicesAsync();

            var options = devices
                .Select(d => new BluetoothDeviceOption(
                    d.Id,
                    BuildBluetoothDisplayName(d)))
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

    private static async Task<IReadOnlyList<DeviceInformation>> ScanBluetoothDevicesAsync()
    {
        const string BleProtocolAqs = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
        var perSelectorTimeout = TimeSpan.FromSeconds(4);

        var selectors = new[]
        {
            BluetoothLEDevice.GetDeviceSelector(),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
            BleProtocolAqs
        };

        var byId = new Dictionary<string, DeviceInformation>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<Exception>();
        var results = await Task.WhenAll(selectors
            .Distinct(StringComparer.Ordinal)
            .Select(selector => ScanSelectorAsync(selector, perSelectorTimeout)));
        var anySelectorSucceeded = false;
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                errors.Add(result.Error);
                continue;
            }

            if (result.TimedOut)
                continue;

            anySelectorSucceeded = true;

            foreach (var d in result.Devices)
            {
                if (string.IsNullOrWhiteSpace(d.Id))
                    continue;

                if (!byId.TryGetValue(d.Id, out var existing))
                {
                    byId[d.Id] = d;
                    continue;
                }

                // Prefer entries with a readable name.
                if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(d.Name))
                    byId[d.Id] = d;
            }
        }

        if (!anySelectorSucceeded && errors.Count > 0)
            throw new InvalidOperationException($"Bluetooth scan failed: {errors[0].Message}", errors[0]);

        return byId.Values.ToList();
    }

    private static async Task<BluetoothSelectorScanResult> ScanSelectorAsync(string selector, TimeSpan timeout)
    {
        try
        {
            var queryTask = DeviceInformation.FindAllAsync(selector).AsTask();
            var completedTask = await Task.WhenAny(queryTask, Task.Delay(timeout));
            if (completedTask != queryTask)
            {
                // Consume potential late exceptions from timed out discovery task.
                _ = queryTask.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new BluetoothSelectorScanResult(Array.Empty<DeviceInformation>(), null, true);
            }

            return new BluetoothSelectorScanResult(await queryTask, null, false);
        }
        catch (Exception ex)
        {
            return new BluetoothSelectorScanResult(Array.Empty<DeviceInformation>(), ex, false);
        }
    }

    private static string BuildBluetoothDisplayName(DeviceInformation device)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "(Unnamed device)" : device.Name.Trim();
        var isPaired = device.Pairing?.IsPaired == true;
        return isPaired ? $"{name} [paired]" : name;
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
    private sealed record BluetoothSelectorScanResult(IReadOnlyList<DeviceInformation> Devices, Exception? Error, bool TimedOut);
}
