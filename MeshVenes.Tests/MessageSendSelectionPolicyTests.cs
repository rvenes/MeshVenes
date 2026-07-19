using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class MessageSendSelectionPolicyTests
{
    [Fact]
    public void SelectOrPrimary_FallsBackWhenThereIsNoSelection()
    {
        var selection = MessageSendSelectionPolicy.SelectOrPrimary(
            hasSelectedChat: false,
            selectedPeerKey: null,
            availablePeerKeys: [null, "0x11223344"],
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.PrimaryChannel, selection.Target.Kind);
        Assert.True(selection.FellBackToPrimary);
    }

    [Fact]
    public void SelectOrPrimary_FallsBackWhenPersistedPeerIsStale()
    {
        var selection = MessageSendSelectionPolicy.SelectOrPrimary(
            hasSelectedChat: true,
            selectedPeerKey: "0x11223344",
            availablePeerKeys: [null, "0x55667788"],
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.PrimaryChannel, selection.Target.Kind);
        Assert.True(selection.FellBackToPrimary);
    }

    [Fact]
    public void SelectOrPrimary_KeepsAvailableDirectMessage()
    {
        var selection = MessageSendSelectionPolicy.SelectOrPrimary(
            hasSelectedChat: true,
            selectedPeerKey: "0x11223344",
            availablePeerKeys: [null, "0x11223344", "0x55667788"],
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.DirectMessage, selection.Target.Kind);
        Assert.Equal((uint)0x11223344, selection.Target.NodeNumber);
        Assert.Equal("0x11223344", selection.Target.PeerKey);
        Assert.False(selection.FellBackToPrimary);
    }

    [Fact]
    public void ResolveTarget_RejectsSelfDm()
    {
        var target = MessageSendSelectionPolicy.ResolveTarget(
            hasSelectedChat: true,
            selectedPeerKey: "0xAABBCCDD",
            connectedNodeIdHex: "aabbccdd");

        Assert.False(target.IsValid);
    }

    [Theory]
    [InlineData("not-a-node")]
    [InlineData("0x")]
    [InlineData("0x00000000")]
    [InlineData("00000000")]
    [InlineData("channel:")]
    [InlineData("channel:-1")]
    [InlineData("channel:abc")]
    public void ResolveTarget_RejectsMalformedOrZeroPeer(string peerKey)
    {
        var target = MessageSendSelectionPolicy.ResolveTarget(
            hasSelectedChat: true,
            selectedPeerKey: peerKey,
            connectedNodeIdHex: "0xaabbccdd");

        Assert.False(target.IsValid);
    }

    [Theory]
    [InlineData(RadioConnectionStatus.Disconnected)]
    [InlineData(RadioConnectionStatus.Connecting)]
    [InlineData(RadioConnectionStatus.Reconnecting)]
    [InlineData(RadioConnectionStatus.Disconnecting)]
    [InlineData(RadioConnectionStatus.Failed)]
    public void CanSend_RejectsEveryUnavailableRadioState(RadioConnectionStatus status)
    {
        Assert.False(MessageSendSelectionPolicy.CanSend(
            status,
            text: "hello",
            MessageSendTarget.Primary));
    }

    [Fact]
    public void ResolveTarget_AndCanSend_AcceptPrimaryChannel()
    {
        var target = MessageSendSelectionPolicy.ResolveTarget(
            hasSelectedChat: true,
            selectedPeerKey: null,
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.PrimaryChannel, target.Kind);
        Assert.True(MessageSendSelectionPolicy.CanSend(
            RadioConnectionStatus.Connected,
            "hello",
            target));
    }

    [Fact]
    public void ResolveTarget_AndCanSend_AcceptConfiguredChannel()
    {
        var target = MessageSendSelectionPolicy.ResolveTarget(
            hasSelectedChat: true,
            selectedPeerKey: "CHANNEL:3",
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.Channel, target.Kind);
        Assert.Equal((uint)3, target.ChannelIndex);
        Assert.Equal("channel:3", target.PeerKey);
        Assert.True(MessageSendSelectionPolicy.CanSend(
            RadioConnectionStatus.Connected,
            "hello",
            target));
    }

    [Fact]
    public void ResolveTarget_AndCanSend_AcceptDirectMessage()
    {
        var target = MessageSendSelectionPolicy.ResolveTarget(
            hasSelectedChat: true,
            selectedPeerKey: "11223344",
            connectedNodeIdHex: "0xaabbccdd");

        Assert.Equal(MessageSendTargetKind.DirectMessage, target.Kind);
        Assert.Equal((uint)0x11223344, target.NodeNumber);
        Assert.Equal("0x11223344", target.PeerKey);
        Assert.True(MessageSendSelectionPolicy.CanSend(
            RadioConnectionStatus.Connected,
            "hello",
            target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanSend_RejectsEmptyText(string? text)
    {
        Assert.False(MessageSendSelectionPolicy.CanSend(
            RadioConnectionStatus.Connected,
            text,
            MessageSendTarget.Primary));
    }
}
