using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshVenes.Protocol;
using Xunit;

namespace MeshVenes.Tests;

public sealed class MeshtasticWireTests
{
    [Fact]
    public void Wrap_ProducesSyncHeaderAndBigEndianLength()
    {
        var msg = new ToRadio { WantConfigId = 1 };
        var payload = msg.ToByteArray();

        var framed = MeshtasticWire.Wrap(msg);

        Assert.Equal(MeshtasticWire.HeaderLength + payload.Length, framed.Length);
        Assert.Equal(0x94, framed[0]);
        Assert.Equal(0xC3, framed[1]);
        Assert.Equal((payload.Length >> 8) & 0xFF, framed[2]);
        Assert.Equal(payload.Length & 0xFF, framed[3]);
        Assert.Equal(payload, framed[MeshtasticWire.HeaderLength..]);
    }

    [Fact]
    public void Wrap_RoundTripsThroughFrameDecoder()
    {
        var msg = new ToRadio { WantConfigId = 42 };
        var decoder = new MeshtasticFrameDecoder();

        var frames = decoder.Feed(MeshtasticWire.Wrap(msg)).ToList();

        Assert.Single(frames);
        Assert.Equal(msg.ToByteArray(), frames[0]);
    }

    [Fact]
    public void MaxToFromRadioPayloadBytes_MatchesFirmwareLimit()
    {
        // Firmware MAX_TO_FROM_RADIO_SIZE; the MQTT proxy drop guard in
        // RadioClient.SendMqttProxyMessageAsync depends on this value.
        Assert.Equal(512, MeshtasticWire.MaxToFromRadioPayloadBytes);
        Assert.Equal(4, MeshtasticWire.HeaderLength);
    }

    [Fact]
    public void OversizedMqttProxyFrame_ExceedsRadioLimit()
    {
        // A broker message bigger than the firmware limit must be detectable
        // from the framed length, exactly as the drop guard checks it.
        var proxy = new MqttClientProxyMessage
        {
            Topic = "msh/2/e/LongFast/!12345678",
            Data = ByteString.CopyFrom(new byte[600])
        };
        var msg = new ToRadio { MqttClientProxyMessage = proxy };

        var framed = MeshtasticWire.Wrap(msg);

        Assert.True(framed.Length > MeshtasticWire.HeaderLength + MeshtasticWire.MaxToFromRadioPayloadBytes);
    }

    [Fact]
    public void TypicalMqttProxyFrame_FitsWithinRadioLimit()
    {
        // A normal LoRa-sized service envelope must never trip the drop guard.
        var proxy = new MqttClientProxyMessage
        {
            Topic = "msh/2/e/LongFast/!12345678",
            Data = ByteString.CopyFrom(new byte[256])
        };
        var msg = new ToRadio { MqttClientProxyMessage = proxy };

        var framed = MeshtasticWire.Wrap(msg);

        Assert.True(framed.Length <= MeshtasticWire.HeaderLength + MeshtasticWire.MaxToFromRadioPayloadBytes);
    }
}
