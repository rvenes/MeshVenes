using System.IO.Ports;
using Meshtastic.Core;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Meshtastic.Transport.Serial;

public sealed class SerialTransport : IRadioTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly object _sync = new();
    private SerialPort? _port;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private int _isDisconnecting;
    private int _isBroken;

    public event Action<string>? Log;
    public event Action<byte[]>? BytesReceived;

    // SerialPort.IsOpen can stay true after a USB unplug, so a broken flag
    // (set on read/write I/O failures) is needed for loss detection.
    public bool IsConnected => _port?.IsOpen == true && Volatile.Read(ref _isBroken) == 0;

    public SerialTransport(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return Task.CompletedTask;

        Interlocked.Exchange(ref _isDisconnecting, 0);
        Interlocked.Exchange(ref _isBroken, 0);

        var port = new SerialPort(_portName, _baudRate)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 131072,
            WriteBufferSize = 8192
        };

        port.Open();

        lock (_sync)
        {
            _port = port;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(port, _receiveCts.Token));
        }

        Log?.Invoke($"Connected to {_portName}");
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        SerialPort? port;
        CancellationTokenSource? receiveCts;
        Task? receiveLoopTask;

        lock (_sync)
        {
            port = _port;
            receiveCts = _receiveCts;
            receiveLoopTask = _receiveLoopTask;
            _port = null;
            _receiveCts = null;
            _receiveLoopTask = null;
        }

        if (port is null)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 1);

        try
        {
            port.DataReceived -= OnDataReceived;
            port.ErrorReceived -= OnErrorReceived;
            port.PinChanged -= OnPinChanged;
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try { receiveCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        if (receiveLoopTask is not null)
        {
            try { await receiveLoopTask.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
        }

        try
        {
            receiveCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try
        {
            port.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        Log?.Invoke("Disconnected");
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen || Volatile.Read(ref _isDisconnecting) != 0)
        {
            Log?.Invoke("TX dropped: serial not connected");
            return Task.CompletedTask;
        }

        try
        {
            port.Write(data, 0, data.Length);
        }
        catch (ObjectDisposedException) { MarkBroken("TX failed: serial disposed"); return Task.CompletedTask; }
        catch (NullReferenceException) { MarkBroken("TX failed: serial unavailable"); return Task.CompletedTask; }
        catch (IOException) { MarkBroken("TX failed: serial I/O unavailable"); return Task.CompletedTask; }
        catch (InvalidOperationException) { MarkBroken("TX failed: serial port closed"); return Task.CompletedTask; }

        Log?.Invoke($"TX {data.Length} bytes");
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(SerialPort port, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && Volatile.Read(ref _isDisconnecting) == 0)
        {
            int n;
            try
            {
                n = await port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                MarkBroken("Serial read failed: port disposed");
                break;
            }
            catch (NullReferenceException)
            {
                MarkBroken("Serial read failed: port unavailable");
                break;
            }
            catch (IOException)
            {
                MarkBroken("Serial read failed: I/O error (cable unplugged?)");
                break;
            }

            if (n <= 0)
                continue;

            if (Volatile.Read(ref _isDisconnecting) != 0)
                break;

            var payload = new byte[n];
            Buffer.BlockCopy(buffer, 0, payload, 0, n);

            BytesReceived?.Invoke(payload);
        }
    }

    private void MarkBroken(string reason)
    {
        if (Interlocked.Exchange(ref _isBroken, 1) != 0)
            return;

        if (Volatile.Read(ref _isDisconnecting) == 0)
            Log?.Invoke(reason);
    }

    // Kept for safe explicit unsubscription during shutdown.
    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e) { }
    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e) { }
    private void OnPinChanged(object sender, SerialPinChangedEventArgs e) { }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
