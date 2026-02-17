using System;
using System.Globalization;
using System.Linq;

namespace MeshtasticWin.Services;

public static class NodeIdentity
{
    public static bool TryGetConnectedNodeNum(out uint nodeNum)
        => TryParseNodeNumFromHex(AppState.ConnectedNodeIdHex, out nodeNum);

    public static bool TryParseNodeNumFromHex(string? idHex, out uint nodeNum)
    {
        nodeNum = 0;
        if (string.IsNullOrWhiteSpace(idHex))
            return false;

        var s = idHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeNum);
    }

    public static string ConnectedNodeLabel()
    {
        var idHex = AppState.ConnectedNodeIdHex;
        if (string.IsNullOrWhiteSpace(idHex))
            return "No connected node";

        var node = AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase));
        if (node is null)
            return idHex;

        var name = !string.IsNullOrWhiteSpace(node.Name) ? node.Name : (node.LongName ?? idHex);
        var shortId = !string.IsNullOrWhiteSpace(node.ShortId) ? node.ShortId : node.IdHex;
        return string.IsNullOrWhiteSpace(shortId) ? name : $"{name} ({shortId})";
    }
}
