using MeshVenes.Models;
using Xunit;

namespace MeshVenes.Tests;

public class MessageLiveTests
{
    private static MessageLive OutgoingDm(uint targetNodeNum = 0x11223344, uint packetId = 42)
        => MessageLive.CreateOutgoing(
            toIdHex: $"0x{targetNodeNum:x8}",
            toName: "Peer",
            text: "hello",
            packetId: packetId,
            dmTargetNodeNum: targetNodeNum);

    private static MessageLive OutgoingBroadcast(uint packetId = 42)
        => MessageLive.CreateOutgoing(
            toIdHex: "0xffffffff",
            toName: "Primary",
            text: "hello",
            packetId: packetId,
            dmTargetNodeNum: 0);

    [Fact]
    public void AckFromIntermediateNode_MarksHeardOnly()
    {
        var msg = OutgoingDm().WithAckFrom(0xdeadbeef);

        Assert.True(msg.IsHeard);
        Assert.False(msg.IsDelivered);
        Assert.False(msg.DeliveryFailed);
    }

    [Fact]
    public void AckFromDmRecipient_MarksDelivered()
    {
        var msg = OutgoingDm(targetNodeNum: 0x11223344).WithAckFrom(0x11223344);

        Assert.True(msg.IsHeard);
        Assert.True(msg.IsDelivered);
        Assert.False(msg.DeliveryFailed);
    }

    [Fact]
    public void BroadcastAck_NeverMarksDelivered()
    {
        var msg = OutgoingBroadcast().WithAckFrom(0x11223344);

        Assert.True(msg.IsHeard);
        Assert.False(msg.IsDelivered);
    }

    [Fact]
    public void DeliveryFailure_SetsFailedAndKeepsHeard()
    {
        var msg = OutgoingDm().WithAckFrom(0xdeadbeef).WithDeliveryFailure("Max retransmit");

        Assert.True(msg.IsHeard);
        Assert.False(msg.IsDelivered);
        Assert.True(msg.DeliveryFailed);
        Assert.Equal("Max retransmit", msg.FailureReason);
    }

    [Fact]
    public void DeliveryFailure_DoesNotOverrideConfirmedDelivery()
    {
        var msg = OutgoingDm(targetNodeNum: 0x11223344).WithAckFrom(0x11223344).WithDeliveryFailure("late NAK");

        Assert.True(msg.IsDelivered);
        Assert.False(msg.DeliveryFailed);
    }

    [Fact]
    public void LateAckFromRecipient_ClearsEarlierFailure()
    {
        var msg = OutgoingDm(targetNodeNum: 0x11223344)
            .WithDeliveryFailure("timeout")
            .WithAckFrom(0x11223344);

        Assert.True(msg.IsDelivered);
        Assert.False(msg.DeliveryFailed);
        Assert.Equal("", msg.FailureReason);
    }

    [Fact]
    public void AckFromOtherNodeAfterFailure_KeepsFailure()
    {
        var msg = OutgoingDm(targetNodeNum: 0x11223344)
            .WithDeliveryFailure("timeout")
            .WithAckFrom(0xdeadbeef);

        Assert.True(msg.IsHeard);
        Assert.False(msg.IsDelivered);
        Assert.True(msg.DeliveryFailed);
    }
}
