using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeshVenes.Models;

namespace MeshVenes.Services;

public static class MessageArchive
{
    private static readonly object _lock = new();

    // %LOCALAPPDATA%\MeshVenes\Logs
    private static string BaseDir => AppDataPaths.LogsPath;

    public static void Append(MessageLive msg, string? channelName = null, string? dmPeerIdHex = null)
    {
        try
        {
            // Line format:
            //   v1: ISO time | header | text
            //   v2: ISO time | header | id=0x........ | text   (when the packet id is known)
            var header = SanitizeField(msg.Header);
            var text = msg.Text.Replace("\r", " ").Replace("\n", " ");
            var line = msg.PacketId != 0
                ? $"{DateTimeOffset.Now:O} | {header} | id=0x{msg.PacketId:x8} | {text}"
                : $"{DateTimeOffset.Now:O} | {header} | {text}";

            AppendLine(line, channelName, dmPeerIdHex);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>
    /// Persists a delivery-status change (ticks / failure) for an already
    /// archived message, so the status survives an app restart. Written as an
    /// append-only STATUS line referencing the packet id; the newest one wins.
    /// </summary>
    public static void AppendStatus(MessageLive msg)
    {
        if (msg.PacketId == 0)
            return;

        try
        {
            var reason = SanitizeField(msg.FailureReason ?? "").Replace(";", ",");
            var line =
                $"{DateTimeOffset.Now:O} | {StatusHeader} | id=0x{msg.PacketId:x8};heard={(msg.IsHeard ? 1 : 0)};delivered={(msg.IsDelivered ? 1 : 0)};failed={(msg.DeliveryFailed ? 1 : 0)};reason={reason}";

            if (msg.IsDirect)
                AppendLine(line, channelName: null, dmPeerIdHex: msg.IsMine ? msg.ToIdHex : msg.FromIdHex);
            else
                AppendLine(line, channelName: msg.ChannelName, dmPeerIdHex: null);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void AppendLine(string line, string? channelName, string? dmPeerIdHex)
    {
        Directory.CreateDirectory(BaseDir);

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var safeChannel = Sanitize(channelName);

        string fileName;

        if (!string.IsNullOrWhiteSpace(dmPeerIdHex))
        {
            // DM per node (peer)
            fileName = $"dm_{Sanitize(dmPeerIdHex)}_{date}.log";
        }
        else if (!string.IsNullOrWhiteSpace(safeChannel))
        {
            // Channel file
            fileName = $"channel_{safeChannel}_{date}.log";
        }
        else
        {
            // Fallback: everything in one file
            fileName = $"all_{date}.log";
        }

        var path = Path.Combine(BaseDir, fileName);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private const string StatusHeader = "STATUS";

    // The field separator is " | "; keep it out of the header (e.g. node names)
    // so old three-field lines and new four-field lines stay parseable.
    private static string SanitizeField(string value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("|", "/");

    public sealed record ArchivedMessage(
        DateTimeOffset When,
        string Header,
        string Text,
        uint PacketId = 0,
        bool Heard = false,
        bool Delivered = false,
        bool Failed = false,
        string FailureReason = "");

    // Layout: one file per day per conversation in %LocalAppData%\MeshVenes\Logs,
    // named channel_<name>_YYYY-MM-DD.log or dm_<peerIdHex>_YYYY-MM-DD.log.
    // Note: logs are shared across all connected radios (not scoped per own node).
    public static IReadOnlyList<ArchivedMessage> ReadRecent(int maxLines, int daysBack = 0, string? channelName = null, string? dmPeerIdHex = null)
    {
        if (maxLines <= 0)
            return Array.Empty<ArchivedMessage>();

        if (daysBack < 0)
            daysBack = 0;

        try
        {
            Directory.CreateDirectory(BaseDir);

            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(0, daysBack));

            var files = EnumerateLogFiles(BaseDir, cutoff, channelName, dmPeerIdHex)
                .OrderByDescending(GetDateSortKey)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var messages = new List<ArchivedMessage>(Math.Min(maxLines, 512));

            // STATUS lines are appended after the message they refer to, so when
            // reading newest-first the freshest status is always seen before (or
            // in a newer file than) its message line.
            var statusByPacketId = new Dictionary<uint, (bool Heard, bool Delivered, bool Failed, string Reason)>();

            foreach (var file in files)
            {
                if (messages.Count >= maxLines)
                    break;

                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }

                // Files are append-only; read from end so we prefer newest entries.
                for (var i = lines.Length - 1; i >= 0 && messages.Count < maxLines; i--)
                {
                    if (TryParseStatusLine(lines[i], out var statusPacketId, out var status))
                    {
                        if (!statusByPacketId.ContainsKey(statusPacketId))
                            statusByPacketId[statusPacketId] = status;
                        continue;
                    }

                    if (TryParseLine(lines[i], out var msg))
                    {
                        if (msg.PacketId != 0 && statusByPacketId.TryGetValue(msg.PacketId, out var s))
                            msg = msg with { Heard = s.Heard, Delivered = s.Delivered, Failed = s.Failed, FailureReason = s.Reason };
                        messages.Add(msg);
                    }
                }
            }

            return messages
                .OrderByDescending(m => m.When)
                .Take(maxLines)
                .ToList();
        }
        catch
        {
            return Array.Empty<ArchivedMessage>();
        }
    }

    public static int DeleteOlderThanDays(int days, string? channelName = null, string? dmPeerIdHex = null)
    {
        if (days < 1)
            days = 1;

        try
        {
            Directory.CreateDirectory(BaseDir);
            var cutoffDate = DateTime.Now.Date.AddDays(-days);
            var deleted = 0;

            foreach (var file in EnumerateLogFiles(BaseDir, cutoffDate, channelName, dmPeerIdHex))
            {
                if (!TryGetFileDate(file, out var fileDate))
                    continue;

                if (fileDate < cutoffDate)
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // Ignore per-file delete errors.
                    }
                }
            }

            return deleted;
        }
        catch
        {
            return 0;
        }
    }

    public static int DeleteAll(string? channelName = null, string? dmPeerIdHex = null)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var deleted = 0;

            foreach (var file in EnumerateLogFiles(BaseDir, DateTime.MinValue, channelName, dmPeerIdHex))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // Ignore per-file delete errors.
                }
            }

            return deleted;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateLogFiles(string baseDir, DateTime cutoffDate, string? channelName, string? dmPeerIdHex)
    {
        if (!Directory.Exists(baseDir))
            yield break;

        // Our standardized file names always end with _YYYY-MM-DD.log (no extra segments).
        var dateGlob = "????-??-??.log";

        if (!string.IsNullOrWhiteSpace(dmPeerIdHex))
        {
            var prefix = $"dm_{Sanitize(dmPeerIdHex)}_";
            foreach (var file in Directory.EnumerateFiles(baseDir, prefix + dateGlob, SearchOption.TopDirectoryOnly))
            {
                if (TryGetFileDate(file, out var date) && date >= cutoffDate)
                    yield return file;
            }
            yield break;
        }

        var safeChannel = Sanitize(channelName);
        if (!string.IsNullOrWhiteSpace(safeChannel))
        {
            var prefix = $"channel_{safeChannel}_";
            foreach (var file in Directory.EnumerateFiles(baseDir, prefix + dateGlob, SearchOption.TopDirectoryOnly))
            {
                if (TryGetFileDate(file, out var date) && date >= cutoffDate)
                    yield return file;
            }
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(baseDir, "*_" + dateGlob, SearchOption.TopDirectoryOnly))
        {
            if (TryGetFileDate(file, out var date) && date >= cutoffDate)
                yield return file;
        }
    }

    private static bool TryParseLine(string raw, out ArchivedMessage message)
    {
        message = new ArchivedMessage(default, "", "");

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split(new[] { " | " }, 4, StringSplitOptions.None);
        if (parts.Length < 3)
            return false;

        if (!DateTimeOffset.TryParse(parts[0], out var when))
            return false;

        var header = parts[1];
        if (string.Equals(header, StatusHeader, StringComparison.Ordinal))
            return false;

        uint packetId = 0;
        string text;
        if (parts.Length >= 4 && TryParsePacketIdField(parts[2], out packetId))
        {
            text = parts[3];
        }
        else
        {
            // v1 line; re-join in case the text itself contained " | ".
            text = parts.Length == 3 ? parts[2] : parts[2] + " | " + parts[3];
        }

        message = new ArchivedMessage(when, header, text, packetId);
        return true;
    }

    private static bool TryParseStatusLine(string raw, out uint packetId, out (bool Heard, bool Delivered, bool Failed, string Reason) status)
    {
        packetId = 0;
        status = default;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split(new[] { " | " }, 3, StringSplitOptions.None);
        if (parts.Length < 3 || !string.Equals(parts[1], StatusHeader, StringComparison.Ordinal))
            return false;

        var heard = false;
        var delivered = false;
        var failed = false;
        var reason = "";

        foreach (var pair in parts[2].Split(';'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = pair[..eq];
            var value = pair[(eq + 1)..];
            switch (key)
            {
                case "id":
                    TryParsePacketIdField(pair, out packetId);
                    break;
                case "heard":
                    heard = value == "1";
                    break;
                case "delivered":
                    delivered = value == "1";
                    break;
                case "failed":
                    failed = value == "1";
                    break;
                case "reason":
                    reason = value;
                    break;
            }
        }

        if (packetId == 0)
            return false;

        status = (heard, delivered, failed, reason);
        return true;
    }

    private static bool TryParsePacketIdField(string field, out uint packetId)
    {
        packetId = 0;

        const string prefix = "id=0x";
        if (!field.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return uint.TryParse(
            field[prefix.Length..],
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out packetId) && packetId != 0;
    }

    private static bool TryGetFileDate(string filePath, out DateTime date)
    {
        date = default;

        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath) ?? "";
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < name.Length - 1)
            {
                var datePart = name[(lastUnderscore + 1)..];
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    date = parsed.Date;
                    return true;
                }
            }

            date = File.GetLastWriteTime(filePath).Date;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime GetDateSortKey(string filePath)
        => TryGetFileDate(filePath, out var date) ? date : DateTime.MinValue;

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        return s.Trim().Replace(' ', '_');
    }
}
