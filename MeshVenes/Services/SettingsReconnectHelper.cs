using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public static class SettingsReconnectHelper
{
    private static readonly SemaphoreSlim ReconnectGate = new(1, 1);
    private static readonly object CancellationSync = new();
    private static CancellationTokenSource? _activeOperationCts;
    private static int _watchdogRunning;
    private static int _shutdownRequested;

    private static bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public static void CancelPendingConnectionAttempts()
    {
        lock (CancellationSync)
        {
            try { _activeOperationCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public static void RequestShutdown()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        CancelPendingConnectionAttempts();
    }

    public static bool IsNotConnectedException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current is InvalidOperationException ioe &&
                ioe.Message.IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> TryReconnectAfterSaveAsync(Action<string>? setStatus = null, CancellationToken ct = default)
    {
        if (IsShutdownRequested)
            return false;

        await ReconnectGate.WaitAsync(ct).ConfigureAwait(false);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (CancellationSync)
            _activeOperationCts = operationCts;
        var operationToken = operationCts.Token;
        Exception? lastError = null;
        var cancelled = false;
        try
        {
            if (IsShutdownRequested)
                return false;

            Log("Settings reconnect: starting.");

            if (RadioClient.Instance.IsConnected)
            {
                Log("Settings reconnect: already connected.");
                return true;
            }

            setStatus?.Invoke("Connecting...");
            await RadioClient.Instance.PrepareForReconnectAsync(operationToken).ConfigureAwait(false);

            const int maxRounds = 10;
            var retryDelay = TimeSpan.FromSeconds(10);

            // Wait for the saved endpoint to actually come back (COM port visible /
            // TCP port accepting) instead of a fixed delay; BLE cannot be probed
            // cheaply, so it falls back to a grace delay.
            setStatus?.Invoke("Waiting for the node to come back...");
            Log("Settings reconnect: waiting for saved endpoint to become available.");
            var endpointReady = await WaitForSavedEndpointAsync(TimeSpan.FromSeconds(60), setStatus, operationToken).ConfigureAwait(false);
            if (endpointReady)
            {
                Log("Settings reconnect: endpoint available, settling before connect.");
                await Task.Delay(TimeSpan.FromSeconds(3), operationToken).ConfigureAwait(false);
            }
            else
            {
                Log("Settings reconnect: endpoint not probeable or not seen, using grace delay.");
                await Task.Delay(TimeSpan.FromSeconds(15), operationToken).ConfigureAwait(false);
            }

            for (var round = 1; round <= maxRounds; round++)
            {
                operationToken.ThrowIfCancellationRequested();
                var candidates = BuildCandidates();
                if (candidates.Count == 0)
                {
                    setStatus?.Invoke("Reconnect skipped: no saved connection endpoint.");
                    Log("Settings reconnect: no saved endpoint found.");
                }
                else
                {
                    Log($"Settings reconnect: round {round}/{maxRounds}, candidates={candidates.Count}.");
                }

                foreach (var candidate in candidates)
                {
                    operationToken.ThrowIfCancellationRequested();
                    setStatus?.Invoke($"Connecting... ({round}/{maxRounds})");

                    try
                    {
                        if (RadioClient.Instance.IsConnected)
                        {
                            Log("Settings reconnect: connected.");
                            return true;
                        }

                        await candidate(operationToken).ConfigureAwait(false);
                        if (RadioClient.Instance.IsConnected)
                        {
                            Log("Settings reconnect: connected.");
                            return true;
                        }
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (RadioClient.Instance.IsConnected)
                        {
                            Log("Settings reconnect: another connection succeeded.");
                            return true;
                        }

                        lastError = ex;
                        setStatus?.Invoke($"Reconnect attempt failed: {ex.Message}");
                        Log("Settings reconnect attempt failed: " + ex.Message);
                    }
                }

                if (round < maxRounds)
                {
                    setStatus?.Invoke($"Reconnect retry in 10s... ({round}/{maxRounds})");
                    Log($"Settings reconnect: retry in 10s ({round}/{maxRounds}).");
                    await Task.Delay(retryDelay, operationToken).ConfigureAwait(false);
                }
            }

            if (lastError is not null)
            {
                setStatus?.Invoke("Reconnect failed: " + lastError.Message);
                Log("Settings reconnect failed: " + lastError.Message);
            }

            return RadioClient.Instance.IsConnected;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            cancelled = true;
            return false;
        }
        catch (Exception ex)
        {
            lastError = ex;
            try { setStatus?.Invoke("Reconnect failed: " + ex.Message); }
            catch { }
            Log("Settings reconnect failed unexpectedly: " + ex.Message);
            return false;
        }
        finally
        {
            lock (CancellationSync)
            {
                if (ReferenceEquals(_activeOperationCts, operationCts))
                    _activeOperationCts = null;
            }

            try
            {
                if (!RadioClient.Instance.IsConnected)
                    RadioClient.Instance.CompleteReconnect(cancelled ? null : lastError?.Message);
                Log("Settings reconnect: finished.");
            }
            catch (Exception ex)
            {
                Log("Settings reconnect cleanup failed: " + ex.Message);
            }
            finally
            {
                ReconnectGate.Release();
            }
        }
    }

    /// <summary>
    /// Tries each saved endpoint (preferred connection type first) exactly once,
    /// without the reboot-oriented waiting used by the post-save reconnect flow.
    /// </summary>
    public static async Task<bool> TryConnectToSavedEndpointOnceAsync(CancellationToken ct = default)
    {
        if (IsShutdownRequested)
            return false;

        await ReconnectGate.WaitAsync(ct).ConfigureAwait(false);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (CancellationSync)
            _activeOperationCts = operationCts;
        var operationToken = operationCts.Token;
        try
        {
            if (IsShutdownRequested)
                return false;

            if (RadioClient.Instance.IsConnected)
                return true;

            foreach (var candidate in BuildCandidates())
            {
                operationToken.ThrowIfCancellationRequested();
                try
                {
                    await candidate(operationToken).ConfigureAwait(false);
                    if (RadioClient.Instance.IsConnected)
                        return true;
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (RadioClient.Instance.IsConnected)
                    {
                        Log("Connect to saved endpoint: another connection succeeded.");
                        return true;
                    }

                    Log("Connect to saved endpoint failed: " + ex.Message);
                }
            }

            return RadioClient.Instance.IsConnected;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            lock (CancellationSync)
            {
                if (ReferenceEquals(_activeOperationCts, operationCts))
                    _activeOperationCts = null;
            }

            ReconnectGate.Release();
        }
    }

    private static Action<Action> BuildRunOnUi()
    {
        var dispatcher = MeshVenes.App.MainWindowInstance?.DispatcherQueue;
        if (dispatcher is null)
            return action => action();

        return action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
                action();
        };
    }

    public static void StartPostSaveReconnectWatchdog(Action<string>? setStatus = null)
    {
        if (IsShutdownRequested)
            return;

        if (Interlocked.CompareExchange(ref _watchdogRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                Log("Settings reconnect watchdog armed (120s window).");
                var deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                while (!IsShutdownRequested && DateTime.UtcNow < deadlineUtc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (IsShutdownRequested)
                        return;

                    if (!RadioClient.Instance.IsConnected)
                    {
                        setStatus?.Invoke("Node reboot detected. Connecting...");
                        Log("Settings reconnect watchdog detected disconnect.");
                        _ = await TryReconnectAfterSaveAsync(setStatus).ConfigureAwait(false);
                        return;
                    }
                }

                Log("Settings reconnect watchdog ended (no disconnect detected).");
            }
            catch (Exception ex)
            {
                Log("Settings reconnect watchdog error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogRunning, 0);
            }
        });
    }

    private static async Task<bool> WaitForSavedEndpointAsync(TimeSpan maxWait, Action<string>? setStatus, CancellationToken ct)
    {
        var serialPort = SettingsStore.GetString(SettingsStore.LastSerialPortKey)?.Trim();
        var tcpHost = SettingsStore.GetString(SettingsStore.LastTcpHostKey)?.Trim();
        var tcpPortText = SettingsStore.GetString(SettingsStore.LastTcpPortKey)?.Trim();
        var preferred = (SettingsStore.GetString(SettingsStore.LastConnectionTypeKey) ?? string.Empty).Trim().ToLowerInvariant();

        var hasSerial = !string.IsNullOrWhiteSpace(serialPort);
        var hasTcp = !string.IsNullOrWhiteSpace(tcpHost) &&
                     int.TryParse(tcpPortText, out var tcpPort) &&
                     tcpPort is >= 1 and <= 65535;

        var probeSerial = hasSerial && preferred is "serial" or "";
        var probeTcp = !probeSerial && hasTcp && preferred is "tcp" or "";
        if (!probeSerial && !probeTcp && preferred == "")
        {
            probeSerial = hasSerial;
            probeTcp = !probeSerial && hasTcp;
        }

        if (!probeSerial && !probeTcp)
            return false;

        // Give the node a moment to actually drop the endpoint first, so a stale
        // still-enumerated COM port is not mistaken for "already back".
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        var deadlineUtc = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadlineUtc)
        {
            ct.ThrowIfCancellationRequested();

            if (probeSerial)
            {
                try
                {
                    if (System.IO.Ports.SerialPort.GetPortNames()
                        .Any(p => string.Equals(p?.Trim(), serialPort, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
            else if (probeTcp)
            {
                try
                {
                    _ = int.TryParse(tcpPortText, out var port);
                    using var client = new System.Net.Sockets.TcpClient();
                    var connectTask = client.ConnectAsync(tcpHost!, port);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(1500, ct)).ConfigureAwait(false);
                    if (completed == connectTask && client.Connected)
                        return true;
                }
                catch
                {
                }
            }

            setStatus?.Invoke("Waiting for the node to come back...");
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        return false;
    }

    private static List<Func<CancellationToken, Task>> BuildCandidates()
    {
        var list = new List<Func<CancellationToken, Task>>();

        var serialPort = SettingsStore.GetString(SettingsStore.LastSerialPortKey)?.Trim();
        var tcpHost = SettingsStore.GetString(SettingsStore.LastTcpHostKey)?.Trim();
        var tcpPortText = SettingsStore.GetString(SettingsStore.LastTcpPortKey)?.Trim();
        var bleId = SettingsStore.GetString(SettingsStore.LastBluetoothDeviceIdKey)?.Trim();
        var preferred = (SettingsStore.GetString(SettingsStore.LastConnectionTypeKey) ?? string.Empty).Trim().ToLowerInvariant();

        var entries = new List<(string kind, Func<CancellationToken, Task> connect)>();

        // Incoming FromRadio processing updates UI-bound collections and must be
        // marshalled to the dispatcher thread; running it inline on the RX pump
        // thread makes the connection die right after it is established.
        var runOnUi = BuildRunOnUi();
        Action<string> logToUi = message => RadioClient.Instance.AddSystemLog(message);

        if (!string.IsNullOrWhiteSpace(serialPort))
        {
            entries.Add(("serial", ct => RadioClient.Instance.ConnectAsync(serialPort, runOnUi, logToUi, ct)));
        }

        if (!string.IsNullOrWhiteSpace(tcpHost) &&
            int.TryParse(tcpPortText, out var tcpPort) &&
            tcpPort is >= 1 and <= 65535)
        {
            entries.Add(("tcp", ct => RadioClient.Instance.ConnectTcpAsync(tcpHost, tcpPort, runOnUi, logToUi, ct)));
        }

        if (!string.IsNullOrWhiteSpace(bleId))
        {
            entries.Add(("ble", ct => RadioClient.Instance.ConnectBluetoothAsync(
                bleId,
                "saved device",
                runOnUi,
                logToUi,
                ct: ct)));
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            foreach (var preferredEntry in entries)
            {
                if (preferredEntry.kind == preferred)
                    list.Add(preferredEntry.connect);
            }
        }

        foreach (var entry in entries)
        {
            if (!list.Contains(entry.connect))
                list.Add(entry.connect);
        }

        return list;
    }

    private static void Log(string message)
        => RadioClient.Instance.AddSystemLog(message);
}
