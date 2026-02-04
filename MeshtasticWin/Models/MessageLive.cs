using System;

namespace MeshtasticWin.Models;

public sealed class MessageLive
{
    public string FromIdHex { get; init; } = "";
    public string FromName { get; init; } = "";

    public string ToIdHex { get; init; } = "";   // "0x........" eller "0xffffffff"
    public string ToName { get; init; } = "";

    public bool IsMine { get; init; }
    public bool IsDirect => !string.IsNullOrWhiteSpace(ToIdHex) &&
                            !string.Equals(ToIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase);

    public string Text { get; init; } = "";
    public string When { get; init; } = "";

    public uint PacketId { get; init; }

    // NYTT: ✓ vs ✓✓
    public bool IsHeard { get; init; }        // minst ein ACK frå kven som helst
    public bool IsDelivered { get; init; }    // ACK frå DM-mottakar (kun for DM)

    // For å vite kven som er DM-mottakar (nodeNum)
    public uint DmTargetNodeNum { get; init; } // 0 om ikkje DM

    public string Header
    {
        get
        {
            if (IsMine) return "Me";

            var id = FromIdHex ?? "";
            var name = FromName ?? "";

            if (string.IsNullOrWhiteSpace(name)) return id;
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return $"{name} ({id})";

            return name;
        }
    }

    public static MessageLive CreateIncoming(string fromIdHex, string fromName, string toIdHex, string toName, string text)
        => new()
        {
            FromIdHex = fromIdHex ?? "",
            FromName = fromName ?? "",
            ToIdHex = toIdHex ?? "",
            ToName = toName ?? "",
            Text = text ?? "",
            When = DateTime.Now.ToString("HH:mm:ss"),
            IsMine = false,
            PacketId = 0,
            IsHeard = false,
            IsDelivered = false,
            DmTargetNodeNum = 0
        };

    public static MessageLive CreateOutgoing(string toIdHex, string toName, string text, uint packetId, uint dmTargetNodeNum)
        => new()
        {
            FromIdHex = "",
            FromName = "",
            ToIdHex = toIdHex ?? "",
            ToName = toName ?? "",
            Text = text ?? "",
            When = DateTime.Now.ToString("HH:mm:ss"),
            IsMine = true,
            PacketId = packetId,
            IsHeard = false,
            IsDelivered = false,
            DmTargetNodeNum = dmTargetNodeNum
        };

    public MessageLive WithAckFrom(uint ackFromNodeNum)
    {
        // broadcast: berre “heard”
        if (!IsDirect || DmTargetNodeNum == 0)
        {
            if (IsHeard) return this;
            return Clone(heard: true, delivered: false);
        }

        // DM:
        // - heard: ACK frå kven som helst
        // - delivered: ACK frå DM-mottakar
        bool deliveredNow = (ackFromNodeNum == DmTargetNodeNum);
        bool heardNow = true;

        if (IsHeard && (IsDelivered || !deliveredNow))
            return this;

        return Clone(heard: heardNow || IsHeard, delivered: IsDelivered || deliveredNow);
    }

    private MessageLive Clone(bool heard, bool delivered)
        => new()
        {
            FromIdHex = FromIdHex,
            FromName = FromName,
            ToIdHex = ToIdHex,
            ToName = ToName,
            IsMine = IsMine,
            Text = Text,
            When = When,
            PacketId = PacketId,
            IsHeard = heard,
            IsDelivered = delivered,
            DmTargetNodeNum = DmTargetNodeNum
        };
}
