using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshtasticWin;

public sealed class SerialTextDecoder
{
    private readonly Decoder _decoder;
    private readonly StringBuilder _sb = new();

    // Enkel ANSI-strip (fargar osv.)
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public SerialTextDecoder(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        _decoder = encoding.GetDecoder();
    }

    /// <summary>
    /// Tek imot byte-chunks og gir attende komplette tekstlinjer.
    /// </summary>
    public IEnumerable<string> Feed(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            yield break;

        var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length)];
        _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);

        _sb.Append(chars);

        while (true)
        {
            var s = _sb.ToString();
            var idx = s.IndexOf('\n');
            if (idx < 0)
                yield break;

            var line = s[..idx].TrimEnd('\r');
            _sb.Clear();
            _sb.Append(s[(idx + 1)..]);

            // fjern ANSI
            line = AnsiRegex.Replace(line, "");

            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }
}
