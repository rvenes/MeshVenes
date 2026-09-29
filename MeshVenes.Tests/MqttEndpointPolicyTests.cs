using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class MqttEndpointPolicyTests
{
    [Theory]
    [InlineData("mqtt.meshtastic.org", false, "mqtt.meshtastic.org", 8883, true)]
    [InlineData("mqtts://broker.example", false, "broker.example", 8883, true)]
    [InlineData("mqtt://broker.example:8884", true, "broker.example", 8884, true)]
    [InlineData("192.168.1.10", false, "192.168.1.10", 1883, false)]
    public void ResolvesTransportWithoutDowngradingTls(string input, bool flag, string host, int port, bool tls)
        => Assert.Equal((host, port, tls), MqttEndpointPolicy.Resolve(input, flag));

    [Theory]
    [InlineData("mqtt://user:password@broker.example")]
    [InlineData("https://broker.example")]
    [InlineData("mqtt://broker.example/path")]
    [InlineData("mqtt://broker.example:0")]
    public void RejectsAmbiguousAddresses(string input)
        => Assert.Throws<InvalidOperationException>(() => MqttEndpointPolicy.Resolve(input, false));
}
