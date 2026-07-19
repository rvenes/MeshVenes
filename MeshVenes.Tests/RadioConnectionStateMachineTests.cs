using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class RadioConnectionStateMachineTests
{
    [Fact]
    public void StartsDisconnected()
    {
        var machine = new RadioConnectionStateMachine();

        Assert.Equal(RadioConnectionStatus.Disconnected, machine.Current.Status);
        Assert.True(machine.Current.CanConnect);
        Assert.False(machine.Current.IsBusy);
        Assert.False(machine.Current.IsConnected);
    }

    [Fact]
    public void RunsNormalConnectionLifecycle()
    {
        var machine = new RadioConnectionStateMachine();
        var changes = new List<RadioConnectionState>();
        machine.Changed += changes.Add;

        machine.TransitionTo(RadioConnectionStatus.Connecting, "COM7");
        machine.TransitionTo(RadioConnectionStatus.Connected);
        machine.TransitionTo(RadioConnectionStatus.Disconnecting);
        machine.TransitionTo(RadioConnectionStatus.Disconnected);

        Assert.Equal(
            [
                RadioConnectionStatus.Connecting,
                RadioConnectionStatus.Connected,
                RadioConnectionStatus.Disconnecting,
                RadioConnectionStatus.Disconnected
            ],
            changes.Select(change => change.Status));
        Assert.Null(machine.Current.Endpoint);
        Assert.Equal(4, machine.Current.Revision);
    }

    [Fact]
    public void KeepsReconnectStateAcrossFailedAttempts()
    {
        var machine = new RadioConnectionStateMachine();

        machine.TransitionTo(RadioConnectionStatus.Reconnecting, "TCP mesh.local:4403");
        var failedAttempt = machine.TransitionTo(
            RadioConnectionStatus.Reconnecting,
            errorMessage: "Connection refused");
        var connected = machine.TransitionTo(RadioConnectionStatus.Connected);

        Assert.Equal("TCP mesh.local:4403", failedAttempt.Endpoint);
        Assert.Equal("Connection refused", failedAttempt.ErrorMessage);
        Assert.True(failedAttempt.IsBusy);
        Assert.True(failedAttempt.CanDisconnect);
        Assert.True(connected.IsConnected);
        Assert.Null(connected.ErrorMessage);
    }

    [Fact]
    public void FailedConnectionCanBeRetried()
    {
        var machine = new RadioConnectionStateMachine();

        machine.TransitionTo(RadioConnectionStatus.Connecting, "COM4");
        var failed = machine.TransitionTo(
            RadioConnectionStatus.Failed,
            errorMessage: "Access denied");
        var retrying = machine.TransitionTo(RadioConnectionStatus.Connecting);

        Assert.True(failed.CanConnect);
        Assert.False(failed.IsBusy);
        Assert.Equal("COM4", retrying.Endpoint);
        Assert.Null(retrying.ErrorMessage);
    }

    [Fact]
    public void ConnectionAttemptCanBeCancelled()
    {
        var machine = new RadioConnectionStateMachine();

        machine.TransitionTo(RadioConnectionStatus.Connecting, "Bluetooth node");
        var cancelled = machine.TransitionTo(RadioConnectionStatus.Disconnected);

        Assert.Equal(RadioConnectionStatus.Disconnected, cancelled.Status);
        Assert.Null(cancelled.Endpoint);
        Assert.True(cancelled.CanConnect);
    }

    [Fact]
    public void ReconnectCanBeExplicitlyDisconnected()
    {
        var machine = new RadioConnectionStateMachine();

        machine.TransitionTo(RadioConnectionStatus.Reconnecting, "COM9");
        machine.TransitionTo(RadioConnectionStatus.Disconnecting);
        var disconnected = machine.TransitionTo(RadioConnectionStatus.Disconnected);

        Assert.Equal(RadioConnectionStatus.Disconnected, disconnected.Status);
        Assert.False(disconnected.CanDisconnect);
    }

    [Fact]
    public void RejectsSkippingConnectionAttempt()
    {
        var machine = new RadioConnectionStateMachine();

        var error = Assert.Throws<InvalidOperationException>(
            () => machine.TransitionTo(RadioConnectionStatus.Connected, "COM3"));

        Assert.Contains("Disconnected -> Connected", error.Message);
        Assert.Equal(RadioConnectionStatus.Disconnected, machine.Current.Status);
    }

    [Fact]
    public void IdenticalTransitionDoesNotRaiseDuplicateEvent()
    {
        var machine = new RadioConnectionStateMachine();
        var eventCount = 0;
        machine.Changed += _ => eventCount++;

        machine.TransitionTo(RadioConnectionStatus.Connecting, "COM8");
        machine.TransitionTo(RadioConnectionStatus.Connecting, "COM8");

        Assert.Equal(1, eventCount);
        Assert.Equal(1, machine.Current.Revision);
    }

    [Fact]
    public void ConditionalTransitionCompletesExpectedReconnect()
    {
        var machine = new RadioConnectionStateMachine();
        var changes = new List<RadioConnectionState>();
        machine.Changed += changes.Add;
        machine.TransitionTo(RadioConnectionStatus.Reconnecting, "COM8");

        var transitioned = machine.TryTransitionFrom(
            RadioConnectionStatus.Reconnecting,
            RadioConnectionStatus.Failed,
            errorMessage: "Reconnect timed out");

        Assert.True(transitioned);
        Assert.Equal(RadioConnectionStatus.Failed, machine.Current.Status);
        Assert.Equal("Reconnect timed out", machine.Current.ErrorMessage);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void ConditionalTransitionDoesNothingAfterConcurrentStateChange()
    {
        var machine = new RadioConnectionStateMachine();
        var eventCount = 0;
        machine.Changed += _ => eventCount++;
        machine.TransitionTo(RadioConnectionStatus.Reconnecting, "COM8");
        machine.TransitionTo(RadioConnectionStatus.Disconnecting);
        machine.TransitionTo(RadioConnectionStatus.Disconnected);

        var transitioned = machine.TryTransitionFrom(
            RadioConnectionStatus.Reconnecting,
            RadioConnectionStatus.Failed,
            errorMessage: "Stale reconnect failure");

        Assert.False(transitioned);
        Assert.Equal(RadioConnectionStatus.Disconnected, machine.Current.Status);
        Assert.Null(machine.Current.ErrorMessage);
        Assert.Equal(3, eventCount);
    }
}
