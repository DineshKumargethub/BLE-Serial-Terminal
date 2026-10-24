using System.Globalization;

namespace BleTerminal.Core;

/// <summary>
/// ST STM32WB over-the-air update (AN5247 / STM32CubeWB "BLE_Ota" loader).
/// Verified against ST's otas_app.c: the 3 address bytes after the command are big-endian and are OR-ed into 0x08000000,
/// raw data is programmed as 64-bit double-words (tail padded with 0xFF), and the device only reboots after the
/// client has acknowledged the confirmation indication that follows the "upload finished" command.
/// </summary>
public static class OtaProtocol
{
    public const uint FlashBase = 0x08000000;
    public const uint DefaultAddress = 0x08007000;

    public const byte CmdStartApplication = 0x02;
    public const byte CmdUploadFinished = 0x07;

    public static readonly Guid BaseAddress = Guid.Parse("0000fe22-8e22-4541-9d4c-21edae82ed19");  // write w/o response
    public static readonly Guid Confirm = Guid.Parse("0000fe23-8e22-4541-9d4c-21edae82ed19");      // indicate
    public static readonly Guid RawData = Guid.Parse("0000fe24-8e22-4541-9d4c-21edae82ed19");      // write w/o response

    /// <summary>command + 24-bit big-endian offset from 0x08000000, e.g. 0x08007000 -> 02 00 70 00.</summary>
    public static byte[] BuildCommand(byte command, uint flashAddress)
    {
        uint offset = flashAddress - FlashBase;
        return new[] { command, (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset };
    }

    /// <summary>"0x08007000", "08007000" or "8007000".</summary>
    public static bool TryParseAddress(string? text, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
        return uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }
}

/// <summary>Sanity checks on a firmware image before it is sent to flash.</summary>
public static class OtaImage
{
    public sealed record CheckResult(string? Error, IReadOnlyList<string> Warnings);

    public static CheckResult Check(byte[] image, uint address)
    {
        var warnings = new List<string>();
        if (image.Length == 0) return new CheckResult("The file is empty.", warnings);
        if (image[0] == (byte)':')
            return new CheckResult("This looks like an Intel HEX file (starts with ':'). The OTA loader needs the raw .bin.", warnings);
        if (address < OtaProtocol.FlashBase || address - OtaProtocol.FlashBase > 0xFFFFFF)
            return new CheckResult("Flash address must be between 0x08000000 and 0x08FFFFFF (STM32 flash starts at 0x08000000).", warnings);
        if (address % 8 != 0)
            return new CheckResult("Flash address must be a multiple of 8 (flash is programmed in 64-bit double-words).", warnings);

        if (address % 0x1000 != 0) warnings.Add("Address is not 4 KB aligned (STM32WB flash pages are 4 KB).");
        if ((long)address + image.Length > 0x08100000L) warnings.Add("The image extends past 0x08100000 (1 MB).");

        if (image.Length < 8)
        {
            warnings.Add("The file is shorter than a Cortex-M vector table.");
        }
        else
        {
            uint sp = BitConverter.ToUInt32(image, 0);
            uint reset = BitConverter.ToUInt32(image, 4);
            if ((sp & 0xFFF00000) != 0x20000000)
                warnings.Add($"First word 0x{sp:X8} is not a RAM address: this may not be a Cortex-M application image.");
            if ((reset & 1) == 0)
                warnings.Add($"Reset vector 0x{reset:X8} is not a Thumb address (bit 0 clear).");
            uint resetAddr = reset & ~1u;
            if (resetAddr < address || resetAddr >= address + (uint)image.Length)
                warnings.Add($"Reset handler 0x{resetAddr:X8} lies outside 0x{address:X8}..0x{address + (uint)image.Length:X8}: the image looks linked for a different flash address.");
        }
        return new CheckResult(null, warnings);
    }
}
