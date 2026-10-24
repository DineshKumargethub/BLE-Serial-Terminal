using System.Text;

namespace BleTerminal.Core;

/// <summary>Plain-text session log mirroring what the terminal shows (RX/TX/info tags, optional timestamps).</summary>
public sealed class SessionLog : IDisposable
{
    readonly StreamWriter _w;
    string? _openTag;

    public string FilePath { get; }

    SessionLog(string path)
    {
        FilePath = path;
        _w = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static SessionLog Open(string folder)
    {
        Directory.CreateDirectory(folder);
        return new SessionLog(LogPaths.Unique(folder, ""));
    }

    static string Prefix(string tag, bool ts) =>
        (ts ? DateTime.Now.ToString("HH:mm:ss.fff") + " " : "") + tag + " ";

    public void Append(string tag, string text, bool timestamps)
    {
        if (_openTag != null && _openTag != tag) { _w.WriteLine(); _openTag = null; }
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            string piece = nl < 0 ? text.Substring(start) : text.Substring(start, nl - start);
            if (piece.Length > 0)
            {
                if (_openTag == null) { _w.Write(Prefix(tag, timestamps)); _openTag = tag; }
                _w.Write(piece);
            }
            if (nl < 0) break;
            if (_openTag != null) { _w.WriteLine(); _openTag = null; }
            else _w.WriteLine(Prefix(tag, timestamps));
            start = nl + 1;
        }
    }

    public void Dispose()
    {
        try { if (_openTag != null) _w.WriteLine(); _w.Dispose(); } catch { }
    }
}

public static class LogPaths
{
    /// <summary>ble_terminal_yyyyMMdd_HHmmss{suffix}.log, with _2, _3... if that name is taken (two sessions in one second must not share a file).</summary>
    public static string Unique(string folder, string suffix)
    {
        string stem = Path.Combine(folder, $"ble_terminal_{DateTime.Now:yyyyMMdd_HHmmss}{suffix}");
        string path = stem + ".log";
        for (int i = 2; File.Exists(path); i++) path = $"{stem}_{i}.log";
        return path;
    }
}

/// <summary>Per-packet byte log: one line per RX notification / TX write with timestamp, direction, length and hex bytes.</summary>
public sealed class RawLog : IDisposable
{
    readonly StreamWriter _w;

    public string FilePath { get; }

    RawLog(string path)
    {
        FilePath = path;
        _w = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static RawLog Open(string folder)
    {
        Directory.CreateDirectory(folder);
        return new RawLog(LogPaths.Unique(folder, "_raw"));
    }

    public void Write(string direction, byte[] data) =>
        _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {direction} [{data.Length}] {TextCodec.ToHex(data)}");

    public void Dispose()
    {
        try { _w.Dispose(); } catch { }
    }
}
