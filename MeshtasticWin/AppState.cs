using System;
using System.Collections.ObjectModel;
using MeshtasticWin.Models;

namespace MeshtasticWin;

public static class AppState
{
    public static ObservableCollection<NodeLive> Nodes { get; } = new();
    public static ObservableCollection<MessageLive> Messages { get; } = new();

    // null = Primary channel (broadcast), elles DM med denne node-id-en (IdHex, t.d. "0xd6c218df")
    public static string? ActiveChatPeerIdHex { get; private set; }

    public static event Action? ActiveChatChanged;

    public static void SetActiveChatPeer(string? peerIdHex)
    {
        // Normaliser litt
        if (string.IsNullOrWhiteSpace(peerIdHex))
            peerIdHex = null;

        if (string.Equals(ActiveChatPeerIdHex, peerIdHex, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveChatPeerIdHex = peerIdHex;
        ActiveChatChanged?.Invoke();
    }
}
