using Meshtastic.Core;
using Meshtastic.Protobufs;
using Meshtastic.Transport.Serial;
using MeshVenes.Parsing;
using MeshVenes.Protocol;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace MeshVenes.Services;

public sealed class RadioClient
{
    public static RadioClient Instance { get; } = new();
    private const uint BroadcastNodeNum = 0xFFFFFFFF;

    private const int MaxLogLines = 500;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TxQueueSpaceWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TxQueueResultWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TxQueueStatusFreshWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransportDisconnectTimeout = TimeSpan.FromSeconds(10);

    private IRadioTransport? _transport;
    private readonly RadioConnectionStateMachine _connectionStateMachine = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _connectCancellationLock = new();
    private CancellationTokenSource? _activeConnectCts;
    private readonly SerialTextDecoder _decoder = new();
    private readonly MeshtasticFrameDecoder _frameDecoder = new();
    private int _disconnecting;
    private readonly object _liveLogLock = new();
    private readonly Queue<string> _liveLogQueue = new();
    private readonly SemaphoreSlim _liveLogSignal = new(0, int.MaxValue);
    private CancellationTokenSource? _liveLogCts;
    private Task? _liveLogTask;
    private string? _liveLogPath;
    private readonly object _rxQueueLock = new();
    private readonly Queue<byte[]> _rxQueue = new();
    private readonly SemaphoreSlim _rxQueueSignal = new(0, int.MaxValue);
    private CancellationTokenSource? _rxPumpCts;
    private Task? _rxPumpTask;
    private Action<string>? _transportLogHandler;
    private Action<byte[]>? _transportBytesHandler;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private readonly AsyncLocal<bool> _isHeartbeatPumpContext = new();
    private uint _heartbeatNonce;
    private readonly SemaphoreSlim _txSendGate = new(1, 1);
    private readonly SemaphoreSlim _txQueueSpaceSignal = new(0, int.MaxValue);
    private readonly object _txQueueLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<int>> _pendingTxQueueResults = new();
    private int? _txQueueFree;
    private int? _txQueueMaxLen;
    private DateTime _lastTxQueueStatusUtc;
    private bool _hasTxQueueStatus;

    public RadioConnectionState ConnectionState => _connectionStateMachine.Current;
    public bool IsConnected => ConnectionState.IsConnected;
    public bool IsReconnecting => ConnectionState.Status == RadioConnectionStatus.Reconnecting;
    public string? PortName => ConnectionState.Endpoint;

    public ObservableCollection<string> LogLines { get; } = new();

    public event Action? ConnectionChanged;

    private RadioClient()
    {
        _connectionStateMachine.Changed += _ => ConnectionChanged?.Invoke();
    }

    public async Task PrepareForReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectInternalAsync(
            finalStatus: RadioConnectionStatus.Reconnecting,
            failureReason: null,
            expectedTransport: null,
            ct).ConfigureAwait(false);
    }

    public void CompleteReconnect(string? failureReason = null)
    {
        _connectionStateMachine.TryTransitionFrom(
            RadioConnectionStatus.Reconnecting,
            string.IsNullOrWhiteSpace(failureReason)
                ? RadioConnectionStatus.Disconnected
                : RadioConnectionStatus.Failed,
            errorMessage: failureReason);
    }

    private static bool IsTransportNotConnectedException(Exception ex)
    {
        return ex is InvalidOperationException ioe &&
               ioe.Message.IndexOf("Not connected", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task MarkConnectionLostAsync(
        IRadioTransport expectedTransport,
        string? reason = null)
    {
        if (Volatile.Read(ref _disconnecting) != 0 ||
            !ReferenceEquals(_transport, expectedTransport))
        {
            return;
        }

        try
        {
            await DisconnectInternalAsync(
                finalStatus: RadioConnectionStatus.Failed,
                failureReason: reason ?? "Connection lost.",
                expectedTransport,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup after transport loss.
        }
    }

    public void ApplyQueueStatus(int? free, int? maxLen, int? res, uint meshPacketId)
    {
        TaskCompletionSource<int>? completion = null;
        int result = 0;

        lock (_txQueueLock)
        {
            _hasTxQueueStatus = true;
            _lastTxQueueStatusUtc = DateTime.UtcNow;

            if (maxLen.HasValue && maxLen.Value >= 0)
                _txQueueMaxLen = maxLen.Value;

            if (free.HasValue)
            {
                _txQueueFree = Math.Max(0, free.Value);
                if (_txQueueFree.Value > 0)
                {
                    try { _txQueueSpaceSignal.Release(); }
                    catch (SemaphoreFullException) { }
                }
            }

            if (meshPacketId != 0 && _pendingTxQueueResults.TryGetValue(meshPacketId, out var pending))
            {
                _pendingTxQueueResults.Remove(meshPacketId);
                completion = pending;
                result = res ?? 0;
            }
        }

        completion?.TrySetResult(result);
    }

    private void ResetTxQueueState(Exception? pendingFailure = null)
    {
        List<TaskCompletionSource<int>> pending;
        lock (_txQueueLock)
        {
            _txQueueFree = null;
            _txQueueMaxLen = null;
            _lastTxQueueStatusUtc = DateTime.MinValue;
            _hasTxQueueStatus = false;
            pending = new List<TaskCompletionSource<int>>(_pendingTxQueueResults.Values);
            _pendingTxQueueResults.Clear();
        }

        foreach (var tcs in pending)
        {
            if (pendingFailure is null)
                tcs.TrySetCanceled();
            else
                tcs.TrySetException(pendingFailure);
        }
    }

    private bool HasFreshQueueStatus()
    {
        lock (_txQueueLock)
        {
            if (!_hasTxQueueStatus)
                return false;

            return (DateTime.UtcNow - _lastTxQueueStatusUtc) <= TxQueueStatusFreshWindow;
        }
    }

    private async Task WaitForTxQueueSpaceAsync()
    {
        if (!HasFreshQueueStatus())
            return;

        var deadlineUtc = DateTime.UtcNow + TxQueueSpaceWaitTimeout;
        while (true)
        {
            int? free;
            lock (_txQueueLock)
                free = _txQueueFree;

            if (!free.HasValue || free.Value > 0)
                return;

            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var signaled = await _txQueueSpaceSignal.WaitAsync(remaining).ConfigureAwait(false);
            if (!signaled)
                return;
        }
    }

    private async Task<int> WaitForQueueResultAsync(uint packetId, TaskCompletionSource<int> resultSource)
    {
        using var timeout = new CancellationTokenSource(TxQueueResultWaitTimeout);
        try
        {
            return await resultSource.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_txQueueLock)
            {
                if (_pendingTxQueueResults.TryGetValue(packetId, out var current) &&
                    ReferenceEquals(current, resultSource))
                {
                    _pendingTxQueueResults.Remove(packetId);
                }
            }

            // If firmware does not report QueueStatus for this packet, fall back
            // to transport-level success to avoid dropping legitimate sends.
            return 0;
        }
    }

    /// <summary>
    /// Maps the firmware's queue rejection code (a Routing.Error value) to an
    /// actionable message instead of a bare packet id.
    /// </summary>
    private static string BuildQueueRejectionMessage(uint packetId, int res)
    {
        var error = (Routing.Types.Error)res;
        var hint = error switch
        {
            Routing.Types.Error.PkiUnknownPubkey or Routing.Types.Error.PkiSendFailPublicKey =>
                "The radio has no public key for that node yet, so it refuses to encrypt a direct message. " +
                "Request node info from the node (or wait until it has been heard with its node info), then try again.",
            Routing.Types.Error.PkiFailed =>
                "Encryption to that node failed (key mismatch). If the node was reflashed, remove it from the node list so a fresh key can be learned.",
            Routing.Types.Error.DutyCycleLimit =>
                "The regional duty cycle limit was reached. Wait a bit before sending again.",
            Routing.Types.Error.TooLarge => "The message is too large to send.",
            Routing.Types.Error.RateLimitExceeded => "Sending too fast; wait a moment and try again.",
            Routing.Types.Error.NoChannel => "The radio has no channel matching this message.",
            _ => null
        };

        var baseText = $"Radio rejected packet 0x{packetId:x8} ({error}).";
        return hint is null ? baseText : $"{baseText} {hint}";
    }

    private async Task<bool> SendPacketWithQueueControlAsync(byte[] framedPayload, uint packetId)
    {
        var transport = _transport;
        if (transport is null || !IsConnected)
            throw new InvalidOperationException("Not connected");

        await _txSendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WaitForTxQueueSpaceAsync().ConfigureAwait(false);

            if (!transport.IsConnected)
            {
                await MarkConnectionLostAsync(transport, "Connection lost.").ConfigureAwait(false);
                return false;
            }

            TaskCompletionSource<int>? queueResult = null;
            if (packetId != 0 && HasFreshQueueStatus())
            {
                queueResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_txQueueLock)
                    _pendingTxQueueResults[packetId] = queueResult;
            }

            try
            {
                await transport.SendAsync(framedPayload).ConfigureAwait(false);
                if (!transport.IsConnected)
                {
                    await MarkConnectionLostAsync(transport, "Connection lost.").ConfigureAwait(false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (queueResult is not null)
                {
                    lock (_txQueueLock)
                    {
                        if (_pendingTxQueueResults.TryGetValue(packetId, out var current) &&
                            ReferenceEquals(current, queueResult))
                        {
                            _pendingTxQueueResults.Remove(packetId);
                        }
                    }
                }

                if (IsTransportNotConnectedException(ex) || !transport.IsConnected)
                {
                    await MarkConnectionLostAsync(transport, ex.Message).ConfigureAwait(false);
                    return false;
                }

                throw;
            }

            if (queueResult is not null)
            {
                lock (_txQueueLock)
                {
                    if (_txQueueFree.HasValue && _txQueueFree.Value > 0)
                        _txQueueFree--;
                }

                var res = await WaitForQueueResultAsync(packetId, queueResult).ConfigureAwait(false);
                if (res != 0)
                    throw new IOException(BuildQueueRejectionMessage(packetId, res));
            }

            return true;
        }
        finally
        {
            _txSendGate.Release();
        }
    }

    public void AddLogFromUiThread(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        LogLines.Insert(0, stamped);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(LogLines.Count - 1);

        AppendToLiveDebugLog(stamped);
    }

    public void AddSystemLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            var dq = MeshVenes.App.MainWindowInstance?.DispatcherQueue;
            if (dq is not null)
            {
                _ = dq.TryEnqueue(() => AddLogFromUiThread(line));
            }
        }
        catch
        {
            // Ignore logging failures; runtime behavior first.
        }
    }

    private void AppendToLiveDebugLog(string line)
    {
        try
        {
            EnsureLiveLogWriterStarted();
            lock (_liveLogLock)
                _liveLogQueue.Enqueue(line);
            _liveLogSignal.Release();
        }
        catch
        {
            // Never let file logging break runtime logging.
        }
    }

    private void EnsureLiveLogWriterStarted()
    {
        lock (_liveLogLock)
        {
            if (_liveLogTask is { IsCompleted: false })
                return;

            _liveLogPath = BuildScopedLiveLogPath();

            _liveLogCts = new CancellationTokenSource();
            var ct = _liveLogCts.Token;
            _liveLogTask = Task.Run(async () =>
            {
                var batch = new List<string>(128);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _liveLogSignal.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    batch.Clear();
                    lock (_liveLogLock)
                    {
                        while (_liveLogQueue.Count > 0 && batch.Count < 512)
                            batch.Add(_liveLogQueue.Dequeue());
                    }

                    if (batch.Count == 0 || string.IsNullOrWhiteSpace(_liveLogPath))
                        continue;

                    try
                    {
                        File.AppendAllLines(_liveLogPath, batch);
                    }
                    catch
                    {
                        // Ignore file write issues; runtime logging must continue.
                    }
                }
            }, ct);
        }
    }

    public void RotateLiveLogForCurrentScope()
    {
        lock (_liveLogLock)
            _liveLogPath = BuildScopedLiveLogPath();
    }

    private static string BuildScopedLiveLogPath()
    {
        var scopedDebugDir = Path.Combine(AppDataPaths.DebugLogsRootPath, AppDataPaths.ActiveNodeScope);
        Directory.CreateDirectory(scopedDebugDir);
        return Path.Combine(scopedDebugDir, $"connect_live_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public async System.Threading.Tasks.Task ConnectAsync(
        string port,
        Action<Action> runOnUi,
        Action<string> logToUi,
        CancellationToken ct = default)
    {
        await ConnectWithTransportAsync(
            new SerialTransport(port),
            port,
            runOnUi,
            logToUi,
            ct).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task ConnectTcpAsync(
        string host,
        int port,
        Action<Action> runOnUi,
        Action<string> logToUi,
        CancellationToken ct = default)
    {
        await ConnectWithTransportAsync(
            new TcpTransport(host, port),
            $"TCP {host}:{port}",
            runOnUi,
            logToUi,
            ct).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task ConnectBluetoothAsync(
        string deviceId,
        string deviceName,
        Action<Action> runOnUi,
        Action<string> logToUi,
        Func<System.Threading.Tasks.Task<string?>>? pinProvider = null,
        CancellationToken ct = default)
    {
        await ConnectWithTransportAsync(
            new BluetoothLeTransport(deviceId, pinProvider),
            $"Bluetooth {deviceName}",
            runOnUi,
            logToUi,
            ct).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
        => DisconnectInternalAsync(
            RadioConnectionStatus.Disconnected,
            failureReason: null,
            expectedTransport: null,
            ct);

    private void CancelActiveConnect()
    {
        lock (_connectCancellationLock)
        {
            try { _activeConnectCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task DisconnectInternalAsync(
        RadioConnectionStatus finalStatus,
        string? failureReason,
        IRadioTransport? expectedTransport,
        CancellationToken ct)
    {
        CancelActiveConnect();
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (expectedTransport is not null && !ReferenceEquals(_transport, expectedTransport))
                return;

            var transport = _transport;
            var endpoint = ConnectionState.Endpoint;
            if (transport is null)
            {
                if (ConnectionState.Status != finalStatus)
                {
                    _connectionStateMachine.TransitionTo(
                        finalStatus,
                        endpoint,
                        failureReason);
                }

                MeshVenes.AppState.SetConnectedNodeIdHex(null);
                return;
            }

            _connectionStateMachine.TransitionTo(
                RadioConnectionStatus.Disconnecting,
                endpoint);
            Interlocked.Exchange(ref _disconnecting, 1);

            if (_transportLogHandler is not null)
                transport.Log -= _transportLogHandler;
            if (_transportBytesHandler is not null)
                transport.BytesReceived -= _transportBytesHandler;

            _transportLogHandler = null;
            _transportBytesHandler = null;
            _transport = null;

            ResetTxQueueState(new IOException(failureReason ?? "Disconnected."));
            StopHeartbeatPump();

            if (transport is SerialTransport &&
                finalStatus == RadioConnectionStatus.Disconnected &&
                transport.IsConnected)
            {
                try
                {
                    var disconnectMsg = ToRadioFactory.CreateDisconnectNotice();
                    var disconnectFrame = MeshtasticWire.Wrap((Google.Protobuf.IMessage)disconnectMsg);
                    await transport.SendAsync(disconnectFrame, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Optional notice; ignore on shutdown.
                }
            }

            StopRxPump();
            try
            {
                await AwaitTransportCleanupAsync(
                    transport.DisconnectAsync(),
                    "Transport disconnect").ConfigureAwait(false);
            }
            finally
            {
                MeshVenes.AppState.SetConnectedNodeIdHex(null);
                _connectionStateMachine.TransitionTo(
                    finalStatus,
                    endpoint,
                    failureReason);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task AwaitTransportCleanupAsync(Task cleanupTask, string operation)
    {
        try
        {
            await cleanupTask.WaitAsync(TransportDisconnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = cleanupTask.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            AddSystemLog(
                $"{operation} timed out after {TransportDisconnectTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async System.Threading.Tasks.Task ConnectWithTransportAsync(
        IRadioTransport transport,
        string endpointName,
        Action<Action> runOnUi,
        Action<string> logToUi,
        CancellationToken ct)
    {
        CancellationTokenSource? connectCts = null;
        var reconnectAttempt = false;
        var gateAcquired = false;
        var retainTransport = false;
        try
        {
            await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
            gateAcquired = true;

            if (IsConnected)
                throw new InvalidOperationException($"Already connected to {PortName}.");

            var status = ConnectionState.Status;
            if (status == RadioConnectionStatus.Disconnecting)
                throw new InvalidOperationException("Cannot connect while disconnecting.");

            reconnectAttempt = status == RadioConnectionStatus.Reconnecting;
            if (!reconnectAttempt)
            {
                _connectionStateMachine.TransitionTo(
                    RadioConnectionStatus.Connecting,
                    endpointName);
            }
            else
            {
                _connectionStateMachine.TransitionTo(
                    RadioConnectionStatus.Reconnecting,
                    endpointName);
            }

            connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_connectCancellationLock)
                _activeConnectCts = connectCts;

            Interlocked.Exchange(ref _disconnecting, 0);
            StopHeartbeatPump();
            ResetTxQueueState();
            _transport = transport;

            StartRxPump(runOnUi, logToUi);

            _transportLogHandler = msg =>
            {
                logToUi(msg);
                if (Volatile.Read(ref _disconnecting) == 0 &&
                    IsConnected &&
                    !transport.IsConnected)
                {
                    _ = MarkConnectionLostAsync(transport, msg);
                }
            };
            _transportBytesHandler = bytes =>
            {
                if (Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
                    return;

                lock (_rxQueueLock)
                    _rxQueue.Enqueue(bytes);

                _rxQueueSignal.Release();
            };

            transport.Log += _transportLogHandler;
            transport.BytesReceived += _transportBytesHandler;

            try
            {
                await transport.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await CleanupFailedConnectAsync(transport, ex).ConfigureAwait(false);

                var cancelled = connectCts.IsCancellationRequested;
                if (reconnectAttempt && !cancelled)
                {
                    _connectionStateMachine.TransitionTo(
                        RadioConnectionStatus.Reconnecting,
                        endpointName,
                        ex.Message);
                }
                else
                {
                    _connectionStateMachine.TransitionTo(
                        cancelled
                            ? RadioConnectionStatus.Disconnected
                            : RadioConnectionStatus.Failed,
                        endpointName,
                        cancelled ? null : ex.Message);
                }

                throw;
            }

            _connectionStateMachine.TransitionTo(
                RadioConnectionStatus.Connected,
                endpointName);
            retainTransport = true;
            MeshVenes.AppState.SetConnectedNodeIdHex(null);

            try
            {
                if (transport is SerialTransport)
                {
                    // Wake the device serial console before the first request,
                    // like the official clients (32 x 0xC3 sync bytes).
                    var wake = new byte[32];
                    Array.Fill(wake, (byte)0xC3);
                    await transport.SendAsync(wake, connectCts.Token).ConfigureAwait(false);
                    await System.Threading.Tasks.Task.Delay(100, connectCts.Token).ConfigureAwait(false);
                }

                var helloMsg = ToRadioFactory.CreateHelloRequest(1u);
                var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)helloMsg);
                await transport.SendAsync(framed, connectCts.Token).ConfigureAwait(false);
                logToUi("Sent ToRadio: WantConfigId=1");
            }
            catch (Exception ex)
            {
                logToUi($"Failed to send ToRadio hello: {ex.Message}");
                if (!transport.IsConnected)
                    _ = MarkConnectionLostAsync(transport, ex.Message);
            }

            if (ReferenceEquals(_transport, transport) && IsConnected)
                StartHeartbeatPump(transport);
        }
        finally
        {
            lock (_connectCancellationLock)
            {
                if (ReferenceEquals(_activeConnectCts, connectCts))
                    _activeConnectCts = null;
            }

            connectCts?.Dispose();
            try
            {
                if (gateAcquired)
                    _connectionGate.Release();
            }
            finally
            {
                if (!retainTransport)
                {
                    try
                    {
                        await AwaitTransportCleanupAsync(
                            transport.DisposeAsync().AsTask(),
                            "Transport disposal").ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }
    }

    private async Task CleanupFailedConnectAsync(IRadioTransport transport, Exception error)
    {
        if (_transportLogHandler is not null)
            transport.Log -= _transportLogHandler;
        if (_transportBytesHandler is not null)
            transport.BytesReceived -= _transportBytesHandler;

        _transportLogHandler = null;
        _transportBytesHandler = null;
        ResetTxQueueState(new IOException("Connection failed.", error));
        StopHeartbeatPump();
        StopRxPump();
        if (ReferenceEquals(_transport, transport))
            _transport = null;

        try
        {
            await AwaitTransportCleanupAsync(
                transport.DisconnectAsync(),
                "Failed connection cleanup").ConfigureAwait(false);
        }
        catch { }
    }

    private void StartRxPump(Action<Action> runOnUi, Action<string> logToUi)
    {
        StopRxPump();

        lock (_rxQueueLock)
            _rxQueue.Clear();

        _rxPumpCts = new CancellationTokenSource();
        var ct = _rxPumpCts.Token;
        _rxPumpTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0)
            {
                try
                {
                    await _rxQueueSignal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                byte[]? bytes = null;
                lock (_rxQueueLock)
                {
                    if (_rxQueue.Count > 0)
                        bytes = _rxQueue.Dequeue();
                }

                if (bytes is null)
                    continue;

                ProcessIncomingBytes(bytes, runOnUi, logToUi);
            }
        }, ct);
    }

    private void StopRxPump()
    {
        var cts = _rxPumpCts;
        var task = _rxPumpTask;
        _rxPumpCts = null;
        _rxPumpTask = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        try { _rxQueueSignal.Release(); } catch { }

        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        if (cts is not null)
        {
            try { cts.Dispose(); } catch { }
        }

        lock (_rxQueueLock)
            _rxQueue.Clear();
    }

    private void StartHeartbeatPump(IRadioTransport transport)
    {
        // Runs for all transports: keeps the serial/TCP PhoneAPI session
        // alive and doubles as periodic connection-loss detection (the
        // IsConnected check below catches BLE drops and USB unplugs).

        StopHeartbeatPump();
        _heartbeatNonce = 0;

        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;

        _heartbeatTask = Task.Run(async () =>
        {
            _isHeartbeatPumpContext.Value = true;
            try
            {
                while (!ct.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0 && IsConnected)
                {
                    try
                    {
                        await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (ct.IsCancellationRequested || Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
                        break;

                    try
                    {
                        if (!transport.IsConnected)
                        {
                            await MarkConnectionLostAsync(transport, "Connection lost.").ConfigureAwait(false);
                            break;
                        }

                        var nonce = unchecked(++_heartbeatNonce);
                        var msg = ToRadioFactory.CreateHeartbeat(nonce);
                        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
                        await transport.SendAsync(framed).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (Volatile.Read(ref _disconnecting) != 0)
                    {
                        break;
                    }
                    catch (NullReferenceException) when (Volatile.Read(ref _disconnecting) != 0)
                    {
                        break;
                    }
                    catch (IOException) when (Volatile.Read(ref _disconnecting) != 0)
                    {
                        break;
                    }
                    catch (InvalidOperationException ex) when (IsTransportNotConnectedException(ex))
                    {
                        await MarkConnectionLostAsync(transport, ex.Message).ConfigureAwait(false);
                        break;
                    }
                    catch
                    {
                        // Ignore heartbeat errors; regular traffic should continue.
                    }
                }
            }
            finally
            {
                _isHeartbeatPumpContext.Value = false;
            }
        }, ct);
    }

    private void StopHeartbeatPump()
    {
        var cts = _heartbeatCts;
        var task = _heartbeatTask;
        _heartbeatCts = null;
        _heartbeatTask = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (task is not null && !_isHeartbeatPumpContext.Value)
        {
            try { task.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        if (cts is not null)
        {
            try { cts.Dispose(); } catch { }
        }
    }

    private void ProcessIncomingBytes(byte[] bytes, Action<Action> runOnUi, Action<string> logToUi)
    {
        if (Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
            return;

        foreach (var frame in _frameDecoder.Feed(bytes))
        {
            if (Volatile.Read(ref _disconnecting) != 0)
                return;

            var handled = FromRadioRouter.TryHandle(frame, runOnUi, logToUi, out var summary);
            if (!handled && ShouldLogProtoSummary(summary))
                logToUi($"PROTOBUF frame (unknown): {summary} ({frame.Length} bytes)");
        }

        foreach (var line in _decoder.Feed(bytes))
        {
            if (Volatile.Read(ref _disconnecting) != 0)
                return;

            if (!LooksLikeDebugText(line))
                continue;

            // Keep serial processing lightweight: only forward high-value lines to UI/parser.
            if (!ShouldForwardTextLine(line))
                continue;

            logToUi($"TXT: {line}");

            runOnUi(() =>
            {
                try { MeshDebugLineParser.Consume(line); }
                catch (Exception ex) { logToUi($"MeshDebugLineParser error: {ex.Message}"); }
            });
        }
    }

    // Broadcast
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text)
        => await SendTextAsync(text, (uint?)null, channel: 0);

    // DM when toNodeNum has a value.
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text, uint? toNodeNum)
        => await SendTextAsync(text, toNodeNum, channel: 0);

    // DM when toNodeNum has a value. Broadcast when toNodeNum is null.
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text, uint? toNodeNum, uint channel)
    {
        if (!IsConnected || _transport is null)
            return 0;

        text = ToRadioFactory.NormalizeTextPayload((text ?? "").Trim(), ToRadioFactory.MaxTextPayloadBytes);
        if (text.Length == 0)
            return 0;

        bool isDm = toNodeNum.HasValue;
        uint to = isDm ? toNodeNum!.Value : 0xFFFFFFFF;

        // Keep ACK enabled for broadcast too. Not all nodes ACK broadcast, but when they do we can show it.
        bool wantAck = true;

        var msg = ToRadioFactory.CreateTextMessage(
            text: text,
            to: to,
            wantAck: wantAck,
            channel: channel,
            out uint packetId);

        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);

        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async System.Threading.Tasks.Task<uint> SendNodeInfoRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            return 0;

        var msg = ToRadioFactory.CreateNodeInfoRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async System.Threading.Tasks.Task<uint> SendWaypointAsync(Waypoint waypoint, uint? toNodeNum = null, uint channel = 0)
    {
        if (!IsConnected || _transport is null)
            return 0;

        if (waypoint is null)
            throw new ArgumentNullException(nameof(waypoint));

        var to = toNodeNum ?? BroadcastNodeNum;
        var msg = ToRadioFactory.CreateWaypointMessage(waypoint, to, channel, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async System.Threading.Tasks.Task<uint> SendPositionRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            return 0;

        var msg = ToRadioFactory.CreatePositionRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async System.Threading.Tasks.Task<uint> SendTraceRouteRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            return 0;

        var msg = ToRadioFactory.CreateTraceRouteRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async System.Threading.Tasks.Task<uint> SendAdminMessageAsync(
        uint toNodeNum,
        AdminMessage adminMessage,
        bool wantResponse = true)
    {
        if (!IsConnected || _transport is null)
            return 0;

        if (adminMessage is null)
            throw new ArgumentNullException(nameof(adminMessage));

        var msg = ToRadioFactory.CreateAdminMessage(toNodeNum, adminMessage, wantResponse, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        var sent = await SendPacketWithQueueControlAsync(framed, packetId);
        return sent ? packetId : 0;
    }

    public async Task<bool> SendMqttProxyMessageAsync(MqttClientProxyMessage proxyMessage)
    {
        if (!IsConnected || _transport is null)
            return false;

        if (proxyMessage is null)
            throw new ArgumentNullException(nameof(proxyMessage));

        var msg = ToRadioFactory.CreateMqttProxyMessage(proxyMessage);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        return await SendPacketWithQueueControlAsync(framed, packetId: 0).ConfigureAwait(false);
    }

    private static bool LooksLikeDebugText(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line.Contains('\uFFFD'))
            return false;

        if (line.Length < 8)
            return false;

        var s = line.TrimStart();

        if (s.StartsWith("INFO", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase)) return true;

        if (line.Contains("[Router]", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("[SerialConsole]", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("[RadioIf]", StringComparison.OrdinalIgnoreCase)) return true;

        if (line.Contains(" | ") && line.Contains('[') && line.Contains(']'))
            return true;

        return false;
    }

    private static bool ShouldForwardTextLine(string line)
    {
        var s = line.TrimStart();
        if (s.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Contains("ToPhone queue is full", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        if (line.Contains("ToPhone queue is full", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool ShouldLogProtoSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        var s = summary.Trim();
        if (s.Equals("other", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
