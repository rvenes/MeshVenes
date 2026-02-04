using System;
using System.IO;
using Google.Protobuf;

namespace MeshtasticWin.Protocol;

/// <summary>
/// Meshtastic serial/TCP wire framing:
/// [0x94][0xC3][LEN_MSB][LEN_LSB] + protobuf payload (LEN bytes)
/// </summary>
public static class MeshtasticWire
{
    private const byte Sync1 = 0x94;
    private const byte Sync2 = 0xC3;

    public static byte[] Wrap(IMessage message)
    {
        var payload = message.ToByteArray();
        if (payload.Length > 65535)
            throw new InvalidOperationException("Payload too large for Meshtastic framing.");

        using var ms = new MemoryStream(4 + payload.Length);
        ms.WriteByte(Sync1);
        ms.WriteByte(Sync2);
        ms.WriteByte((byte)((payload.Length >> 8) & 0xFF)); // MSB
        ms.WriteByte((byte)(payload.Length & 0xFF));        // LSB
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }
}
