using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MeshVenes.Services;

/// <summary>Current-user DPAPI records. Legacy files are migrated atomically on their next write.</summary>
[SupportedOSPlatform("windows")]
public static class LocalProtectedFile
{
    private const string Prefix = "MV-DPAPI1:";
    private static readonly object Gate = new();

    private static string Protect(string text) => Prefix + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser)) + Environment.NewLine;

    public static string ReadAllText(string path)
    {
        lock (Gate)
        {
            var content = File.ReadAllText(path);
            if (!content.StartsWith(Prefix, StringComparison.Ordinal)) return content;
            var result = new StringBuilder();
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Damaged protected local data.");
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(line[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
                try { result.Append(Encoding.UTF8.GetString(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            return result.ToString();
        }
    }

    public static string[] ReadAllLines(string path, Encoding? encoding = null)
    {
        using var reader = new StringReader(ReadAllText(path));
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) lines.Add(line);
        return lines.ToArray();
    }

    public static void WriteAllText(string path, string text)
    {
        lock (Gate)
        {
            if (File.Exists(path)) ReadAllText(path); // Refuse to overwrite data this Windows account cannot decrypt.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, Protect(text), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    public static void AppendAllText(string path, string text, Encoding? encoding = null)
    {
        lock (Gate)
        {
            if (File.Exists(path))
            {
                using var reader = new StreamReader(path);
                var header = new char[Prefix.Length];
                if (reader.ReadBlock(header, 0, header.Length) != header.Length || new string(header) != Prefix)
                {
                    reader.Dispose();
                    WriteAllText(path, File.ReadAllText(path) + text);
                    return;
                }
                var firstRecord = reader.ReadLine() ?? throw new InvalidDataException("Damaged protected local data.");
                var verified = ProtectedData.Unprotect(Convert.FromBase64String(firstRecord), null, DataProtectionScope.CurrentUser);
                CryptographicOperations.ZeroMemory(verified);
            }
            File.AppendAllText(path, Protect(text), Encoding.UTF8);
        }
    }

    public static void AppendAllLines(string path, IEnumerable<string> lines) => AppendAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    public static void WriteAllLines(string path, IEnumerable<string> lines) => WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
}
