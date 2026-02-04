using System.IO.Ports;
using Meshtastic.Core;

namespace Meshtastic.Transport.Serial;

public sealed class SerialTransport : IRadioTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;

    public event Action<string>? Log;
    public event Action<byte[]>? BytesReceived;

    public bool IsConnected => _port?.IsOpen == true;

    public SerialTransport(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return Task.CompletedTask;

        _port = new SerialPort(_portName, _baudRate)
        {
            DtrEnable = true,
            RtsEnable = true
        };

        _port.DataReceived += OnDataReceived;
        _port.Open();

        Log?.Invoke($"Connected to {_portName}");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (_port is null)
            return Task.CompletedTask;

        _port.DataReceived -= OnDataReceived;

        if (_port.IsOpen)
            _port.Close();

        _port.Dispose();
        _port = null;

        Log?.Invoke("Disconnected");
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        _port!.Write(data, 0, data.Length);

        Log?.Invoke($"TX {data.Length} bytes");
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (!IsConnected)
            return;

        int n = _port!.BytesToRead;
        if (n <= 0)
            return;

        var buffer = new byte[n];
        _port.Read(buffer, 0, n);

        BytesReceived?.Invoke(buffer);
        Log?.Invoke($"RX {n} bytes");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
