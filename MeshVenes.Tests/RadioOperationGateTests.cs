using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class RadioOperationGateTests
{
    public static TheoryData<RadioConnectionStatus, string> UnavailableStates => new()
    {
        { RadioConnectionStatus.Disconnected, RadioOperationGate.DisconnectedMessage },
        { RadioConnectionStatus.Connecting, RadioOperationGate.ConnectingMessage },
        { RadioConnectionStatus.Reconnecting, RadioOperationGate.ReconnectingMessage },
        { RadioConnectionStatus.Disconnecting, RadioOperationGate.DisconnectingMessage },
        { RadioConnectionStatus.Failed, RadioOperationGate.FailedMessage }
    };

    [Theory]
    [MemberData(nameof(UnavailableStates))]
    public void DeniesOperationsInEveryUnavailableState(
        RadioConnectionStatus status,
        string expectedMessage)
    {
        var result = RadioOperationGate.Evaluate(new RadioConnectionState(status));

        Assert.False(result.IsAllowed);
        Assert.Equal(expectedMessage, result.UnavailableMessage);
    }

    [Fact]
    public void AllowsConnectedOperationWithoutNodeIdentityRequirement()
    {
        var result = RadioOperationGate.Evaluate(
            new RadioConnectionState(RadioConnectionStatus.Connected));

        Assert.True(result.IsAllowed);
        Assert.Null(result.UnavailableMessage);
    }

    [Fact]
    public void DeniesConnectedOperationWhenRequiredNodeIdentityIsMissing()
    {
        var result = RadioOperationGate.Evaluate(
            new RadioConnectionState(RadioConnectionStatus.Connected),
            requiresConnectedNodeIdentity: true,
            hasConnectedNodeIdentity: false);

        Assert.False(result.IsAllowed);
        Assert.Equal(
            RadioOperationGate.MissingConnectedNodeIdentityMessage,
            result.UnavailableMessage);
    }

    [Fact]
    public void AllowsConnectedOperationWhenRequiredNodeIdentityIsAvailable()
    {
        var result = RadioOperationGate.Evaluate(
            new RadioConnectionState(RadioConnectionStatus.Connected),
            requiresConnectedNodeIdentity: true,
            hasConnectedNodeIdentity: true);

        Assert.True(result.IsAllowed);
        Assert.Null(result.UnavailableMessage);
    }

    [Fact]
    public void ConnectionStateTakesPriorityOverNodeIdentity()
    {
        var result = RadioOperationGate.Evaluate(
            new RadioConnectionState(RadioConnectionStatus.Reconnecting),
            requiresConnectedNodeIdentity: true,
            hasConnectedNodeIdentity: false);

        Assert.False(result.IsAllowed);
        Assert.Equal(RadioOperationGate.ReconnectingMessage, result.UnavailableMessage);
    }
}
