using System;
using System.Collections.Generic;
using System.Globalization;

namespace MeshVenes.Services;

public enum MessageSendTargetKind
{
    None,
    PrimaryChannel,
    Channel,
    DirectMessage
}

public sealed record MessageSendTarget(
    MessageSendTargetKind Kind,
    uint ChannelIndex = 0,
    uint? NodeNumber = null,
    string? PeerKey = null)
{
    public static MessageSendTarget None { get; } = new(MessageSendTargetKind.None);
    public static MessageSendTarget Primary { get; } = new(MessageSendTargetKind.PrimaryChannel);

    public bool IsValid => Kind != MessageSendTargetKind.None;
}

public sealed record MessageChatSelection(
    MessageSendTarget Target,
    bool FellBackToPrimary);

public static class MessageSendSelectionPolicy
{
    private const string ChannelPeerPrefix = "channel:";

    public static MessageSendTarget ResolveTarget(
        bool hasSelectedChat,
        string? selectedPeerKey,
        string? connectedNodeIdHex)
    {
        if (!hasSelectedChat)
            return MessageSendTarget.None;

        var peerKey = selectedPeerKey?.Trim();
        if (string.IsNullOrEmpty(peerKey))
            return MessageSendTarget.Primary;

        if (peerKey.StartsWith(ChannelPeerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rawIndex = peerKey[ChannelPeerPrefix.Length..];
            if (!uint.TryParse(rawIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var channelIndex))
                return MessageSendTarget.None;

            return channelIndex == 0
                ? MessageSendTarget.Primary
                : new MessageSendTarget(
                    MessageSendTargetKind.Channel,
                    ChannelIndex: channelIndex,
                    PeerKey: $"{ChannelPeerPrefix}{channelIndex}");
        }

        if (!TryParseNodeId(peerKey, out var nodeNumber))
            return MessageSendTarget.None;

        if (TryParseNodeId(connectedNodeIdHex, out var connectedNodeNumber) &&
            nodeNumber == connectedNodeNumber)
        {
            return MessageSendTarget.None;
        }

        return new MessageSendTarget(
            MessageSendTargetKind.DirectMessage,
            NodeNumber: nodeNumber,
            PeerKey: $"0x{nodeNumber:x8}");
    }

    public static MessageChatSelection SelectOrPrimary(
        bool hasSelectedChat,
        string? selectedPeerKey,
        IEnumerable<string?> availablePeerKeys,
        string? connectedNodeIdHex)
    {
        var requested = ResolveTarget(hasSelectedChat, selectedPeerKey, connectedNodeIdHex);
        if (requested.IsValid && IsAvailable(requested, availablePeerKeys))
            return new MessageChatSelection(requested, FellBackToPrimary: false);

        return new MessageChatSelection(MessageSendTarget.Primary, FellBackToPrimary: true);
    }

    public static bool CanSend(
        RadioConnectionStatus connectionStatus,
        string? text,
        MessageSendTarget target)
    {
        return connectionStatus == RadioConnectionStatus.Connected &&
               !string.IsNullOrWhiteSpace(text) &&
               target.IsValid;
    }

    private static bool IsAvailable(
        MessageSendTarget requested,
        IEnumerable<string?> availablePeerKeys)
    {
        if (availablePeerKeys is null)
            return false;

        foreach (var availablePeerKey in availablePeerKeys)
        {
            var available = ResolveTarget(
                hasSelectedChat: true,
                availablePeerKey,
                connectedNodeIdHex: null);

            if (TargetsMatch(requested, available))
                return true;
        }

        return false;
    }

    private static bool TargetsMatch(MessageSendTarget left, MessageSendTarget right)
    {
        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            MessageSendTargetKind.PrimaryChannel => true,
            MessageSendTargetKind.Channel => left.ChannelIndex == right.ChannelIndex,
            MessageSendTargetKind.DirectMessage => left.NodeNumber == right.NodeNumber,
            _ => false
        };
    }

    private static bool TryParseNodeId(string? peerKey, out uint nodeNumber)
    {
        nodeNumber = 0;
        if (string.IsNullOrWhiteSpace(peerKey))
            return false;

        var rawNodeId = peerKey.Trim();
        if (rawNodeId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            rawNodeId = rawNodeId[2..];

        return uint.TryParse(
                   rawNodeId,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out nodeNumber) &&
               nodeNumber != 0;
    }
}
