using MeshtasticWin.Models;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeshtasticWin.Parsing;

public static class MeshDebugLineParser
{
    // Matchar fr=0xd6c218df eller "from 0xd6c218df"
    private static readonly Regex FrRegex =
        new(@"(?:\bfr=0x(?<id>[0-9a-fA-F]+)\b|\bfrom 0x(?<id>[0-9a-fA-F]+)\b)",
            RegexOptions.Compiled);

    private static readonly Regex SnrRegex =
        new(@"\bnSNR=(?<snr>-?\d+(\.\d+)?)\b", RegexOptions.Compiled);

    private static readonly Regex RssiRegex =
        new(@"\bnRSSI=(?<rssi>-?\d+)\b", RegexOptions.Compiled);

    private static readonly Regex HopRegex =
        new(@"\bHopLim=(?<hop>\d+)\b", RegexOptions.Compiled);

    private static readonly Regex ChRegex =
        new(@"\bCh=0x(?<ch>[0-9a-fA-F]+)\b", RegexOptions.Compiled);

    public static void Consume(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var m = FrRegex.Match(line);
        if (!m.Success)
            return;

        var id = m.Groups["id"].Value.ToLowerInvariant();
        var idHex = $"0x{id}";

        // Finn eller lag node
        var node = AppState.Nodes.FirstOrDefault(n => n.IdHex == idHex);
        if (node is null)
        {
            node = new NodeLive(idHex);
            AppState.Nodes.Insert(0, node);
        }

        node.Touch();

        // Hent ut signal-data om dei finst
        var snrM = SnrRegex.Match(line);
        if (snrM.Success)
            node.SNR = snrM.Groups["snr"].Value;

        var rssiM = RssiRegex.Match(line);
        if (rssiM.Success)
            node.RSSI = rssiM.Groups["rssi"].Value;

        // Lag ei kort “Sub”-linje som liknar Mac-appen
        string sub = "Seen on mesh";
        var hopM = HopRegex.Match(line);
        var chM = ChRegex.Match(line);

        if (snrM.Success || rssiM.Success || hopM.Success || chM.Success)
        {
            sub = "Signal";
            if (snrM.Success) sub += $" • SNR {node.SNR}";
            if (rssiM.Success) sub += $" • RSSI {node.RSSI}";
            if (hopM.Success) sub += $" • HopLim {hopM.Groups["hop"].Value}";
            if (chM.Success) sub += $" • Ch 0x{chM.Groups["ch"].Value}";
        }

        node.Sub = sub;
    }
}
