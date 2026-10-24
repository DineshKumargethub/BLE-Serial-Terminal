using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BleTerminal.Core;

public enum TxNewline { None, CR, LF, CRLF }
public enum RxNewline { Auto, CR, LF, CRLF }
public enum WriteMode { Auto, WithResponse, WithoutResponse }
public enum ViewMode { Text, Hex, Both }
public enum SendMode { Text, Hex }

public sealed class Macro
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool IsHex { get; set; }
    public bool AppendNewline { get; set; } = true;

    public Macro Clone() => (Macro)MemberwiseClone();
}

/// <summary>A named set of UUIDs for a device family (custom GATT UART layout), kept in the settings file.</summary>
public sealed class SavedProfile
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string Notify { get; set; } = "";
    public string Write { get; set; } = "";
}

public sealed class AppSettings
{
    public const int MacroCount = 12;

    public TxNewline TxNl { get; set; } = TxNewline.CRLF;
    public RxNewline RxNl { get; set; } = RxNewline.Auto;
    public WriteMode WriteOpt { get; set; } = WriteMode.Auto;
    public ViewMode View { get; set; } = ViewMode.Text;
    public SendMode Send { get; set; } = SendMode.Text;
    public bool LocalEcho { get; set; } = true;
    public bool Timestamps { get; set; } = false;
    public bool AutoScroll { get; set; } = true;
    public bool AutoReconnect { get; set; } = false;
    public bool HideUnnamed { get; set; } = true;
    public bool AutoLog { get; set; } = false;
    public string LogFolder { get; set; } = "";
    public string Profile { get; set; } = "Auto-detect";
    public string CustomService { get; set; } = "";
    public string CustomNotify { get; set; } = "";
    public string CustomWrite { get; set; } = "";
    public double FontSize { get; set; } = 14;
    public bool DarkMode { get; set; } = false;
    public string OtaFile { get; set; } = "";
    public string OtaAddress { get; set; } = "0x08007000";
    public int OtaDelayMs { get; set; } = 2;
    public bool RawLog { get; set; } = false;
    public int RepeatIntervalMs { get; set; } = 1000;
    public int RepeatCount { get; set; } = 0;
    public List<SavedProfile> SavedProfiles { get; set; } = new();
    public List<Macro> Macros { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BleTerminal", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch { /* corrupt file -> defaults */ }
        var d = new AppSettings();
        d.Normalize();
        return d;
    }

    /// <summary>Import: unlike <see cref="Load"/> this throws, so a wrong file is reported instead of silently resetting everything.</summary>
    public static AppSettings LoadStrict(string path)
    {
        string text = File.ReadAllText(path);
        using (var doc = JsonDocument.Parse(text))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Not a BLE Terminal configuration file.");
            var known = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
            if (!doc.RootElement.EnumerateObject().Any(p => known.Contains(p.Name)))
                throw new InvalidDataException("Not a BLE Terminal configuration file (no known settings found).");
        }
        var s = JsonSerializer.Deserialize<AppSettings>(text, Json)
                ?? throw new InvalidDataException("The file is empty.");
        s.Normalize();
        return s;
    }

    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Copies every settable property from <paramref name="other"/> (used by import; the settings object itself is shared).</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                p.SetValue(this, p.GetValue(other));
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are best-effort */ }
    }

    void Normalize()
    {
        Macros ??= new List<Macro>();
        while (Macros.Count < MacroCount) Macros.Add(new Macro { Name = $"M{Macros.Count + 1}" });
        if (Macros.Count > MacroCount) Macros.RemoveRange(MacroCount, Macros.Count - MacroCount);
        FontSize = Math.Clamp(FontSize, 8, 40);
        RepeatIntervalMs = Math.Clamp(RepeatIntervalMs, 10, 3_600_000);
        if (RepeatCount < 0) RepeatCount = 0;
        SavedProfiles ??= new List<SavedProfile>();
        SavedProfiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Service));
    }
}
