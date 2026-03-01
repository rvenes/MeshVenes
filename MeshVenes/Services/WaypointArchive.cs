using MeshVenes.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MeshVenes.Services;

public static class WaypointArchive
{
    private static readonly object _gate = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    public static void LoadIntoCollection(ObservableCollection<WaypointLive> target)
    {
        if (target is null)
            return;

        var loaded = LoadCurrentScope()
            .Where(w => !w.IsExpired)
            .OrderByDescending(w => w.LastUpdatedUtc)
            .ToArray();

        target.Clear();
        foreach (var waypoint in loaded)
            target.Add(waypoint);
    }

    public static void SaveCurrentScope(IEnumerable<WaypointLive> waypoints)
    {
        var path = AppDataPaths.WaypointsPath;
        var snapshots = (waypoints ?? Array.Empty<WaypointLive>())
            .Where(w => !w.IsExpired)
            .OrderBy(w => w.WaypointId)
            .Select(FromLive)
            .ToArray();

        var json = JsonSerializer.Serialize(snapshots, s_jsonOptions);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
    }

    private static IReadOnlyList<WaypointLive> LoadCurrentScope()
    {
        var path = AppDataPaths.WaypointsPath;
        if (!File.Exists(path))
            return Array.Empty<WaypointLive>();

        try
        {
            string json;
            lock (_gate)
                json = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<WaypointLive>();

            var snapshots = JsonSerializer.Deserialize<List<WaypointSnapshot>>(json, s_jsonOptions);
            if (snapshots is null || snapshots.Count == 0)
                return Array.Empty<WaypointLive>();

            var result = new List<WaypointLive>(snapshots.Count);
            foreach (var snapshot in snapshots)
            {
                if (snapshot is null || snapshot.WaypointId == 0)
                    continue;

                var waypoint = new WaypointLive(snapshot.WaypointId)
                {
                    SourceNodeNum = snapshot.SourceNodeNum,
                    SourceIdHex = snapshot.SourceIdHex ?? "",
                    Name = snapshot.Name ?? "",
                    Description = snapshot.Description ?? "",
                    Latitude = snapshot.Latitude,
                    Longitude = snapshot.Longitude,
                    IconCodepoint = snapshot.IconCodepoint,
                    LockedToNodeNum = snapshot.LockedToNodeNum,
                    ChannelIndex = snapshot.ChannelIndex,
                    LastUpdatedUtc = snapshot.LastUpdatedUtc > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(snapshot.LastUpdatedUtc).UtcDateTime
                        : DateTime.UtcNow,
                    ExpireUtc = snapshot.ExpireUtc > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(snapshot.ExpireUtc).UtcDateTime
                        : null
                };

                result.Add(waypoint);
            }

            return result;
        }
        catch
        {
            return Array.Empty<WaypointLive>();
        }
    }

    private static WaypointSnapshot FromLive(WaypointLive waypoint)
    {
        var updatedUtc = waypoint.LastUpdatedUtc.Kind == DateTimeKind.Utc
            ? waypoint.LastUpdatedUtc
            : waypoint.LastUpdatedUtc.ToUniversalTime();

        var expireUtc = waypoint.ExpireUtc.HasValue
            ? (waypoint.ExpireUtc.Value.Kind == DateTimeKind.Utc
                ? waypoint.ExpireUtc.Value
                : waypoint.ExpireUtc.Value.ToUniversalTime())
            : (DateTime?)null;

        return new WaypointSnapshot
        {
            WaypointId = waypoint.WaypointId,
            SourceNodeNum = waypoint.SourceNodeNum,
            SourceIdHex = waypoint.SourceIdHex,
            Name = waypoint.Name,
            Description = waypoint.Description,
            Latitude = waypoint.Latitude,
            Longitude = waypoint.Longitude,
            IconCodepoint = waypoint.IconCodepoint,
            LockedToNodeNum = waypoint.LockedToNodeNum,
            ChannelIndex = waypoint.ChannelIndex,
            LastUpdatedUtc = new DateTimeOffset(updatedUtc).ToUnixTimeSeconds(),
            ExpireUtc = expireUtc.HasValue ? new DateTimeOffset(expireUtc.Value).ToUnixTimeSeconds() : 0
        };
    }

    private sealed class WaypointSnapshot
    {
        public uint WaypointId { get; set; }
        public uint SourceNodeNum { get; set; }
        public string? SourceIdHex { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public uint IconCodepoint { get; set; }
        public uint LockedToNodeNum { get; set; }
        public uint ChannelIndex { get; set; }
        public long LastUpdatedUtc { get; set; }
        public long ExpireUtc { get; set; }
    }
}
