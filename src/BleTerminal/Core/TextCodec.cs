using System.Text;

namespace BleTerminal.Core;

public static class TextCodec
{
    public static byte[] NewlineBytes(TxNewline n) => n switch
    {
        TxNewline.CR => new byte[] { 13 },
        TxNewline.LF => new byte[] { 10 },
        TxNewline.CRLF => new byte[] { 13, 10 },
        _ => Array.Empty<byte>()
    };

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Accepts "01 A0 ff", "0x01,0x02", "01:02:03", "0102".</summary>
    public static bool TryParseHex(string text, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        string t = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        var digits = new StringBuilder();
        foreach (char c in t)
        {
            if (char.IsWhiteSpace(c) || c == ',' || c == ':' || c == '-') continue;
            if (!Uri.IsHexDigit(c)) { error = $"'{c}' is not a hex digit"; return false; }
            digits.Append(c);
        }
        if (digits.Length % 2 != 0) { error = "odd number of hex digits"; return false; }
        var result = new byte[digits.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
        bytes = result;
        return true;
    }

    /// <summary>Classic hex dump: 16 bytes per row, hex column, then printable ASCII ('.' for the rest). No trailing newline.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, int perLine = 16)
    {
        var sb = new StringBuilder();
        for (int off = 0; off < data.Length; off += perLine)
        {
            int n = Math.Min(perLine, data.Length - off);
            if (off > 0) sb.Append('\n');
            for (int i = 0; i < perLine; i++)
            {
                if (i < n) sb.Append(data[off + i].ToString("X2")); else sb.Append("  ");
                if (i < perLine - 1) sb.Append(' ');
            }
            sb.Append("  |");
            for (int i = 0; i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    public static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond < 1000 ? $"{bytesPerSecond:F0} B/s"
        : bytesPerSecond < 1_000_000 ? $"{bytesPerSecond / 1000:F1} kB/s"
        : $"{bytesPerSecond / 1_000_000:F2} MB/s";

    /// <summary>48-bit Bluetooth address -> "AA:BB:CC:DD:EE:FF".</summary>
    public static string ToMac(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}

/// <summary>Turns the RX stream into '\n'-only text according to the chosen newline setting. Chunk-safe.</summary>
public sealed class RxNormalizer
{
    public RxNewline Mode { get; set; } = RxNewline.Auto;
    bool _swallowLf;   // Auto: LF directly after a CR is part of the same line break
    bool _sawCr;       // CRLF: a CR is pending, waiting for LF

    public string Process(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (Mode)
            {
                case RxNewline.Auto:
                    if (c == '\r') { sb.Append('\n'); _swallowLf = true; }
                    else if (c == '\n') { if (_swallowLf) _swallowLf = false; else sb.Append('\n'); }
                    else { _swallowLf = false; AppendVisible(sb, c); }
                    break;

                case RxNewline.CR:
                    if (c == '\r') sb.Append('\n');
                    else if (c != '\n') AppendVisible(sb, c);
                    break;

                case RxNewline.LF:
                    if (c == '\n') sb.Append('\n');
                    else if (c != '\r') AppendVisible(sb, c);
                    break;

                case RxNewline.CRLF:
                    if (c == '\r') _sawCr = true;
                    else if (c == '\n') { if (_sawCr) sb.Append('\n'); _sawCr = false; }
                    else { _sawCr = false; AppendVisible(sb, c); }
                    break;
            }
        }
        return sb.ToString();
    }

    public void Reset() { _swallowLf = false; _sawCr = false; }

    static void AppendVisible(StringBuilder sb, char c)
    {
        if (c < 0x20 && c != '\t') return;   // drop other control characters (NUL, ESC, ...)
        sb.Append(c);
    }
}

/// <summary>Streaming UTF-8 decoder + newline normalizer.</summary>
public sealed class RxTextDecoder
{
    readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    public RxNormalizer Normalizer { get; } = new();

    /// <summary>Forget a half-received multi-byte character and any pending newline state (new connection).</summary>
    public void Reset() { _decoder.Reset(); Normalizer.Reset(); }

    public string Decode(byte[] data)
    {
        var chars = new char[_decoder.GetCharCount(data, 0, data.Length, false)];
        int n = _decoder.GetChars(data, 0, data.Length, chars, 0, false);
        return Normalizer.Process(new string(chars, 0, n));
    }
}

public static class UuidParser
{
    const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

    /// <summary>Accepts 16-bit ("FFE0", "0xFFE0"), 32-bit, or full 128-bit UUIDs.</summary>
    public static bool TryParse(string? text, out Guid uuid)
    {
        uuid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
        if (t.Length == 4 && t.All(Uri.IsHexDigit)) t = "0000" + t + BaseSuffix;
        else if (t.Length == 8 && t.All(Uri.IsHexDigit)) t = t + BaseSuffix;
        return Guid.TryParse(t, out uuid);
    }
}
