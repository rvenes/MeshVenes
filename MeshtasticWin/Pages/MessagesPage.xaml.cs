using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeshtasticWin.Models;
using MeshtasticWin.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

namespace MeshtasticWin.Pages;

public sealed partial class MessagesPage : Page
{
    public ObservableCollection<MessageVm> ViewMessages { get; } = new();
    public ObservableCollection<ChatTargetVm> ChatTargets { get; } = new();

    private bool _suppressPickerEvent;

    public MessagesPage()
    {
        InitializeComponent();

        MeshtasticWin.AppState.Messages.CollectionChanged += Messages_CollectionChanged;
        MeshtasticWin.AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        MeshtasticWin.AppState.ActiveChatChanged += ActiveChatChanged;

        RebuildChatTargets();
        SyncPickerToActiveChat();
        RebuildView();
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RebuildView);

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildChatTargets();
            SyncPickerToActiveChat();
        });

    private void ActiveChatChanged()
        => DispatcherQueue.TryEnqueue(() =>
        {
            SyncPickerToActiveChat();
            RebuildView();
        });

    private void RebuildChatTargets()
    {
        ChatTargets.Clear();

        // Primary (broadcast)
        ChatTargets.Add(ChatTargetVm.Primary());

        // DM targets
        foreach (var n in MeshtasticWin.AppState.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.IdHex))
                continue;

            if (ChatTargets.Any(x => string.Equals(x.PeerIdHex, n.IdHex, StringComparison.OrdinalIgnoreCase)))
                continue;

            ChatTargets.Add(ChatTargetVm.ForNode(n));
        }
    }

    private void SyncPickerToActiveChat()
    {
        _suppressPickerEvent = true;

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        ChatTargetVm? match;
        if (string.IsNullOrWhiteSpace(peer))
            match = ChatTargets.FirstOrDefault(x => x.PeerIdHex is null);
        else
            match = ChatTargets.FirstOrDefault(x => string.Equals(x.PeerIdHex, peer, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            ChatPicker.SelectedItem = match;

        _suppressPickerEvent = false;
    }

    private void ChatPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickerEvent)
            return;

        if (ChatPicker.SelectedItem is not ChatTargetVm target)
            return;

        MeshtasticWin.AppState.SetActiveChatPeer(target.PeerIdHex);
    }

    private void RebuildView()
    {
        ViewMessages.Clear();

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        foreach (var m in MeshtasticWin.AppState.Messages)
        {
            if (string.IsNullOrWhiteSpace(peer))
            {
                // Primary view: berre broadcast
                if (!m.IsDirect)
                    ViewMessages.Add(MessageVm.From(m));
            }
            else
            {
                // DM view: meldingar som er mellom oss og peeren (inn eller ut)
                if (m.IsDirect &&
                    (string.Equals(m.FromIdHex, peer, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m.ToIdHex, peer, StringComparison.OrdinalIgnoreCase)))
                {
                    ViewMessages.Add(MessageVm.From(m));
                }
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
        => await SendNowAsync();

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendNowAsync();
        }
    }

    private static bool TryParseNodeNumFromHex(string idHex, out uint nodeNum)
    {
        nodeNum = 0;

        if (string.IsNullOrWhiteSpace(idHex))
            return false;

        var s = idHex.Trim();

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeNum);
    }

    private async System.Threading.Tasks.Task SendNowAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        InputBox.Text = "";

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        // Primary defaults
        uint? toNodeNum = null;
        uint dmTargetNodeNum = 0;

        string toIdHex = "0xffffffff";
        string toName = "Primary";

        // DM
        if (!string.IsNullOrWhiteSpace(peer))
        {
            toIdHex = peer;

            if (TryParseNodeNumFromHex(peer, out var u))
            {
                toNodeNum = u;
                dmTargetNodeNum = u;
            }

            var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, peer, StringComparison.OrdinalIgnoreCase));

            toName = node?.Name ?? peer;
        }

        // Send -> få packetId for ACK-match
        var packetId = await RadioClient.Instance.SendTextAsync(text, toNodeNum);

        // Lokal melding (så kan FromRadioRouter merke ✓ / ✓✓ seinare)
        var local = MessageLive.CreateOutgoing(
            toIdHex: toIdHex,
            toName: toName,
            text: text,
            packetId: packetId,
            dmTargetNodeNum: dmTargetNodeNum);

        MeshtasticWin.AppState.Messages.Insert(0, local);

        // Arkiv
        if (string.IsNullOrWhiteSpace(peer))
            MessageArchive.Append(local, channelName: "Primary");
        else
            MessageArchive.Append(local, dmPeerIdHex: peer);
    }
}

public sealed class ChatTargetVm
{
    public string Display { get; set; } = "";
    public string? PeerIdHex { get; set; } // null = Primary

    public static ChatTargetVm Primary()
        => new() { Display = "Primary channel", PeerIdHex = null };

    public static ChatTargetVm ForNode(NodeLive n)
    {
        var name = n.Name;
        var shortId = n.ShortId;

        var display =
            string.IsNullOrWhiteSpace(shortId)
                ? $"DM: {name}"
                : $"DM: {name} ({shortId})";

        return new ChatTargetVm
        {
            Display = display,
            PeerIdHex = n.IdHex
        };
    }
}

public sealed class MessageVm
{
    public string Header { get; set; } = "";
    public string Text { get; set; } = "";
    public string When { get; set; } = "";

    public Visibility HeardVisible { get; set; } = Visibility.Collapsed;
    public Visibility DeliveredVisible { get; set; } = Visibility.Collapsed;

    public static MessageVm From(MessageLive m)
        => new()
        {
            Header = m.Header,
            Text = m.Text,
            When = m.When,
            HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed,
            DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed
        };
}
