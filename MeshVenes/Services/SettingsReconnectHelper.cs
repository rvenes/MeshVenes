using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeshVenes.Services;

public static class SettingsReconnectHelper
{
    private static readonly SemaphoreSlim ReconnectGate = new(1, 1);
    private static int _watchdogRunning;

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
        await ReconnectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log("Settings reconnect: starting.");
            RadioClient.Instance.SetReconnectingState(true);

            if (RadioClient.Instance.IsConnected)
            {
                Log("Settings reconnect: already connected.");
                return true;
            }

            setStatus?.Invoke("Connecting...");
            try { await RadioClient.Instance.DisconnectAsync().ConfigureAwait(false); } catch { }

            Exception? lastError = null;
            const int maxRounds = 10;
            var retryDelay = TimeSpan.FromSeconds(20);

            setStatus?.Invoke("Connecting in 20s...");
            Log("Settings reconnect: waiting 20s before first attempt.");
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);

            for (var round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
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
                    ct.ThrowIfCancellationRequested();
                    setStatus?.Invoke($"Connecting... ({round}/{maxRounds})");

                    try
                    {
                        if (RadioClient.Instance.IsConnected)
                        {
                            Log("Settings reconnect: connected.");
                            return true;
                        }

                        await candidate(ct).ConfigureAwait(false);
                        if (RadioClient.Instance.IsConnected)
                        {
                            Log("Settings reconnect: connected.");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        setStatus?.Invoke($"Reconnect attempt failed: {ex.Message}");
                        Log("Settings reconnect attempt failed: " + ex.Message);
                        try { await RadioClient.Instance.DisconnectAsync().ConfigureAwait(false); } catch { }
                    }
                }

                if (round < maxRounds)
                {
                    setStatus?.Invoke($"Reconnect retry in 20s... ({round}/{maxRounds})");
                    Log($"Settings reconnect: retry in 20s ({round}/{maxRounds}).");
                    await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                }
            }

            if (lastError is not null)
            {
                setStatus?.Invoke("Reconnect failed: " + lastError.Message);
                Log("Settings reconnect failed: " + lastError.Message);
            }

            return RadioClient.Instance.IsConnected;
        }
        finally
        {
            RadioClient.Instance.SetReconnectingState(false);
            Log("Settings reconnect: finished.");
            ReconnectGate.Release();
        }
    }

    public static void StartPostSaveReconnectWatchdog(Action<string>? setStatus = null)
    {
        if (Interlocked.CompareExchange(ref _watchdogRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                Log("Settings reconnect watchdog armed (120s window).");
                var deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                while (DateTime.UtcNow < deadlineUtc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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

    private static List<Func<CancellationToken, Task>> BuildCandidates()
    {
        var list = new List<Func<CancellationToken, Task>>();

        var serialPort = SettingsStore.GetString(SettingsStore.LastSerialPortKey)?.Trim();
        var tcpHost = SettingsStore.GetString(SettingsStore.LastTcpHostKey)?.Trim();
        var tcpPortText = SettingsStore.GetString(SettingsStore.LastTcpPortKey)?.Trim();
        var bleId = SettingsStore.GetString(SettingsStore.LastBluetoothDeviceIdKey)?.Trim();
        var preferred = (SettingsStore.GetString(SettingsStore.LastConnectionTypeKey) ?? string.Empty).Trim().ToLowerInvariant();

        var entries = new List<(string kind, Func<CancellationToken, Task> connect)>();

        if (!string.IsNullOrWhiteSpace(serialPort))
        {
            entries.Add(("serial", ct => RadioClient.Instance.ConnectAsync(serialPort, a => a(), _ => { })));
        }

        if (!string.IsNullOrWhiteSpace(tcpHost) &&
            int.TryParse(tcpPortText, out var tcpPort) &&
            tcpPort is >= 1 and <= 65535)
        {
            entries.Add(("tcp", ct => RadioClient.Instance.ConnectTcpAsync(tcpHost, tcpPort, a => a(), _ => { })));
        }

        if (!string.IsNullOrWhiteSpace(bleId))
        {
            entries.Add(("ble", ct => RadioClient.Instance.ConnectBluetoothAsync(bleId, "saved device", a => a(), _ => { })));
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
