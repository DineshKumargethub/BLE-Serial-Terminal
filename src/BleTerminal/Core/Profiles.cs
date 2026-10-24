namespace BleTerminal.Core;

/// <summary>A GATT "serial" service layout. Notify/Write may be null = pick by characteristic properties.</summary>
public sealed class GattProfile
{
    public string Name { get; init; } = "";
    public Guid? Service { get; init; }
    public Guid? Notify { get; init; }
    public Guid? Write { get; init; }
    public bool IsAuto { get; init; }
    public bool IsCustom { get; init; }
    public bool IsSaved { get; init; }

    public override string ToString() => Name;
}

public static class Profiles
{
    static Guid G(string s) => Guid.Parse(s);

    public static readonly GattProfile Auto = new() { Name = "Auto-detect", IsAuto = true };

    public static readonly GattProfile Nordic = new()
    {
        Name = "Nordic UART (NUS)",
        Service = G("6e400001-b5a3-f393-e0a9-e50e24dcca9e"),
        Write = G("6e400002-b5a3-f393-e0a9-e50e24dcca9e"),
        Notify = G("6e400003-b5a3-f393-e0a9-e50e24dcca9e"),
    };

    public static readonly GattProfile Hm10 = new()
    {
        Name = "HM-10 / CC254x (FFE0)",
        Service = G("0000ffe0-0000-1000-8000-00805f9b34fb"),
        Write = G("0000ffe1-0000-1000-8000-00805f9b34fb"),
        Notify = G("0000ffe1-0000-1000-8000-00805f9b34fb"),
    };

    public static readonly GattProfile Microchip = new()
    {
        Name = "Microchip RN4870/71 / BM70",
        Service = G("49535343-fe7d-4ae5-8fa9-9fafd205e455"),
        Notify = G("49535343-1e4d-4bd9-ba61-23c647249616"),
        Write = G("49535343-8841-43f4-a8d4-ecbe34729bb3"),
    };

    // Telit TIO: characteristics picked by properties (first notify / first write in the service).
    public static readonly GattProfile Telit = new()
    {
        Name = "Telit Bluemod (TIO)",
        Service = G("0000fefb-0000-1000-8000-00805f9b34fb"),
    };

    public static readonly GattProfile Custom = new() { Name = "Custom UUIDs", IsCustom = true };

    /// <summary>Profiles tried, in order, by Auto-detect.</summary>
    public static readonly GattProfile[] Known = { Nordic, Hm10, Microchip, Telit };

    /// <summary>Everything shown in the profile dropdown.</summary>
    public static readonly GattProfile[] All = { Auto, Nordic, Hm10, Microchip, Telit, Custom };
}
