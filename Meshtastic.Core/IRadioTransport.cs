namespace Meshtastic.Core;

public interface IRadioTransport : IAsyncDisposable
{
    event Action<string>? Log;
    event Action<byte[]>? BytesReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(byte[] data, CancellationToken ct = default);
}
