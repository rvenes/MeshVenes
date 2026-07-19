using System;
using System.Globalization;
using System.Linq;

namespace MeshVenes.Services;

public static class NodeIdentity
{
    public static bool TryGetConnectedNodeNum(out uint nodeNum)
    {
        var hasNodeIdentity = TryParseNodeNumFromHex(
            AppState.GetEffectiveAdminTargetNodeIdHex(),
            out nodeNum);
        var availability = RadioOperationGate.Evaluate(
            RadioClient.Instance.ConnectionState,
            requiresConnectedNodeIdentity: true,
            hasConnectedNodeIdentity: hasNodeIdentity);

        if (availability.IsAllowed)
            return true;

        nodeNum = 0;
        return false;
    }

    public static bool TryParseNodeNumFromHex(string? idHex, out uint nodeNum)
    {
        nodeNum = 0;
        if (string.IsNullOrWhiteSpace(idHex))
            return false;

        var s = idHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeNum) &&
               nodeNum != 0;
    }

    public static string ConnectedNodeLabel()
    {
        var idHex = AppState.GetEffectiveAdminTargetNodeIdHex();
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
