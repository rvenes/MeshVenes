using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MeshVenes.Services;

public static class MyNodeStore
{
    private const string MyNodesKey = "MyNodesJson";

    private static readonly object _lock = new();
    private static HashSet<string>? _myNodeIds;

    public static bool Contains(string? idHex)
    {
        var normalizedId = Normalize(idHex);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return false;

        lock (_lock)
        {
            EnsureLoaded();
            return _myNodeIds!.Contains(normalizedId);
        }
    }

    public static bool Set(string? idHex, bool isMyNode)
    {
        var normalizedId = Normalize(idHex);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return false;

        lock (_lock)
        {
            EnsureLoaded();

            var changed = isMyNode
                ? _myNodeIds!.Add(normalizedId)
                : _myNodeIds!.Remove(normalizedId);

            if (changed)
                Persist();

            return changed;
        }
    }

    private static void EnsureLoaded()
    {
        if (_myNodeIds is not null)
            return;

        try
        {
            var raw = SettingsStore.GetString(MyNodesKey);
            var parsed = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<List<string>>(raw);

            _myNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (parsed is null)
                return;

            foreach (var id in parsed)
            {
                var normalizedId = Normalize(id);
                if (!string.IsNullOrWhiteSpace(normalizedId))
                    _myNodeIds.Add(normalizedId);
            }
        }
        catch
        {
            _myNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Persist()
    {
        try
        {
            var sorted = _myNodeIds is null
                ? new List<string>()
                : new List<string>(_myNodeIds);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            SettingsStore.SetString(MyNodesKey, JsonSerializer.Serialize(sorted));
        }
        catch
        {
            // Ignore persistence errors and keep runtime state intact.
        }
    }

    private static string? Normalize(string? idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex))
            return null;

        return idHex.Trim().ToLowerInvariant();
    }
}
