using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MeshtasticWin.Models;

namespace MeshtasticWin.Services;

public static class MessageArchive
{
    private static readonly object _lock = new();

    // %LOCALAPPDATA%\MeshtasticWin\Logs
    private static string BaseDir => AppDataPaths.LogsPath;

    public static void Append(MessageLive msg, string? channelName = null, string? dmPeerIdHex = null)
    {
        try
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

            // Simple, robust line format
            // ISO time | header | text
            var line =
                $"{DateTimeOffset.Now:O} | {msg.Header} | {msg.Text.Replace("\r", " ").Replace("\n", " ")}";

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    public sealed record ArchivedMessage(DateTimeOffset When, string Header, string Text);

    // Standardized format (forward-looking only):
    // - Per connected node scope: %LocalAppData%\\...\\MeshtasticWin\\NodeLogs\\<nodeId>\\Logs
    // - Per day: channel_Primary_YYYY-MM-DD.log or dm_<peerIdHex>_YYYY-MM-DD.log
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
                    if (TryParseLine(lines[i], out var msg))
                        messages.Add(msg);
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

        var parts = raw.Split(new[] { " | " }, 3, StringSplitOptions.None);
        if (parts.Length < 3)
            return false;

        if (!DateTimeOffset.TryParse(parts[0], out var when))
            return false;

        message = new ArchivedMessage(when, parts[1], parts[2]);
        return true;
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
