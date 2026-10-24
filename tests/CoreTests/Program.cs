using System.Text;
using BleTerminal.Core;

int fails = 0;
void Check(bool ok, string name)
{
    if (!ok) { fails++; Console.WriteLine("FAIL: " + name); }
    else Console.WriteLine("ok:   " + name);
}
string Norm(RxNewline mode, params string[] chunks)
{
    var n = new RxNormalizer { Mode = mode };
    return string.Concat(chunks.Select(n.Process));
}

// ---- hex ----
Check(TextCodec.TryParseHex("01 A0 ff", out var b1, out _) && b1.SequenceEqual(new byte[] { 1, 0xA0, 0xFF }), "hex spaced");
Check(TextCodec.TryParseHex("0x01,0x02", out var b2, out _) && b2.SequenceEqual(new byte[] { 1, 2 }), "hex 0x/comma");
Check(TextCodec.TryParseHex("01:02:03", out var b3, out _) && b3.Length == 3, "hex colon");
Check(TextCodec.TryParseHex("0102", out var b4, out _) && b4.Length == 2, "hex packed");
Check(!TextCodec.TryParseHex("ABC", out _, out var e1) && e1.Contains("odd"), "hex odd digits rejected");
Check(!TextCodec.TryParseHex("ZZ", out _, out _), "hex invalid char rejected");
Check(TextCodec.ToHex(new byte[] { 0x48, 0x65, 0x0A }) == "48 65 0A", "ToHex");
Check(TextCodec.ToHex(Array.Empty<byte>()) == "", "ToHex empty");

// ---- newline bytes ----
Check(TextCodec.NewlineBytes(TxNewline.CRLF).SequenceEqual(new byte[] { 13, 10 }), "tx CRLF");
Check(TextCodec.NewlineBytes(TxNewline.None).Length == 0, "tx None");

// ---- RX normalizer ----
Check(Norm(RxNewline.Auto, "a\r\nb\rc\nd") == "a\nb\nc\nd", "rx auto mixed");
Check(Norm(RxNewline.Auto, "a\r", "\nb") == "a\nb", "rx auto CRLF split across chunks");
Check(Norm(RxNewline.Auto, "a\r\n\r\nb") == "a\n\nb", "rx auto blank line");
Check(Norm(RxNewline.CR, "a\r\nb") == "a\nb", "rx CR");
Check(Norm(RxNewline.LF, "a\r\nb") == "a\nb", "rx LF");
Check(Norm(RxNewline.CRLF, "a\r\nb\nc\rd") == "a\nbcd", "rx CRLF strict");
Check(Norm(RxNewline.CRLF, "a\r", "\nb") == "a\nb", "rx CRLF split across chunks");
Check(Norm(RxNewline.Auto, "x\0y\u001b[z") == "xy[z", "rx control chars dropped");
Check(Norm(RxNewline.Auto, "a\tb") == "a\tb", "rx tab kept");

// ---- UTF-8 streaming ----
var dec = new RxTextDecoder();
var euro = Encoding.UTF8.GetBytes("é€");   // C3 A9 E2 82 AC
string part = dec.Decode(new byte[] { euro[0] }) + dec.Decode(new[] { euro[1], euro[2], euro[3] }) + dec.Decode(new[] { euro[4] });
Check(part == "é€", "utf8 split multibyte");

// ---- UUIDs ----
Check(UuidParser.TryParse("FFE0", out var u1) && u1 == Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb"), "uuid 16-bit");
Check(UuidParser.TryParse("0xffe1", out var u2) && u2 == Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb"), "uuid 0x16-bit");
Check(UuidParser.TryParse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E", out var u3) && u3 == Profiles.Nordic.Service, "uuid 128-bit");
Check(UuidParser.TryParse("6e400001", out var u4) && u4 == Guid.Parse("6e400001-0000-1000-8000-00805f9b34fb"), "uuid 32-bit");
Check(!UuidParser.TryParse("nope", out _) && !UuidParser.TryParse("", out _) && !UuidParser.TryParse(null, out _), "uuid invalid");

// ---- MAC ----
Check(TextCodec.ToMac(0xAABBCCDDEEFF) == "AA:BB:CC:DD:EE:FF", "mac format");
Check(TextCodec.ToMac(0x0000000000A1) == "00:00:00:00:00:A1", "mac leading zeros");

// ---- profiles ----
Check(Profiles.All.Length == 6 && Profiles.Known.All(p => p.Service.HasValue), "profiles sane");

// ---- settings roundtrip ----
string tmp = Path.Combine(Path.GetTempPath(), "bleterm_test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
string sp = Path.Combine(tmp, "settings.json");
var s = AppSettings.Load(sp);
Check(s.Macros.Count == 12 && s.Macros[0].Name == "M1", "settings defaults (12 macros)");
s.TxNl = TxNewline.LF; s.View = ViewMode.Hex; s.Macros[2] = new Macro { Name = "PING", Command = "AA 55", IsHex = true };
s.Save(sp);
var s2 = AppSettings.Load(sp);
Check(s2.TxNl == TxNewline.LF && s2.View == ViewMode.Hex && s2.Macros[2].Command == "AA 55" && s2.Macros[2].IsHex, "settings roundtrip");
File.WriteAllText(sp, "{ not json");
Check(AppSettings.Load(sp).Macros.Count == 12, "settings corrupt -> defaults");

// ---- session log ----
string logDir = Path.Combine(tmp, "logs");
string logPath;
using (var log = SessionLog.Open(logDir))
{
    logPath = log.FilePath;
    log.Append("RX", "hel", false);
    log.Append("RX", "lo\nwor", false);
    log.Append("TX", "AT\n", false);
    log.Append("RX", "\n", false);
    log.Append("--", "done\n", false);
}
var lines = File.ReadAllLines(logPath);
Check(lines.SequenceEqual(new[] { "RX hello", "RX wor", "TX AT", "RX ", "-- done" }), "session log lines: " + string.Join("|", lines));
Directory.Delete(tmp, true);

// ---- decoder reset / unique log names ----
var dec2 = new RxTextDecoder();
dec2.Decode(new byte[] { 0xC3 });          // half of "é"
dec2.Reset();
Check(dec2.Decode(new byte[] { 0x41 }) == "A", "decoder reset drops a half-received character");
string logDir2 = Path.Combine(Path.GetTempPath(), "bleterm_log_" + Guid.NewGuid().ToString("N"));
using (var l1 = SessionLog.Open(logDir2))
using (var l2 = SessionLog.Open(logDir2))
    Check(l1.FilePath != l2.FilePath, "two logs opened in the same second get different files");
Directory.Delete(logDir2, true);

// ---- hex dump / rate / signal ----
Check(TextCodec.HexDump(new byte[] { 0x48, 0x69, 0x00 }) == "48 69 00                                         |Hi.|", "hexdump single row: [" + TextCodec.HexDump(new byte[] { 0x48, 0x69, 0x00 }) + "]");
var dump = TextCodec.HexDump(Enumerable.Range(0, 20).Select(i => (byte)(0x41 + i)).ToArray());
Check(dump.Split('\n').Length == 2 && dump.Split('\n')[1].EndsWith("|QRST|"), "hexdump wraps at 16 bytes");
Check(TextCodec.HexDump(Array.Empty<byte>()) == "", "hexdump empty");
Check(TextCodec.FormatRate(512) == "512 B/s" && TextCodec.FormatRate(12345) == "12.3 kB/s" && TextCodec.FormatRate(2_500_000) == "2.50 MB/s", "format rate");
var meter = new RateMeter();
var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
Check(meter.Sample(0, t0) == 0, "rate first sample is 0");
Check(Math.Abs(meter.Sample(1000, t0.AddSeconds(2)) - 500) < 0.001, "rate 1000 B in 2 s = 500 B/s");
Check(Math.Abs(meter.Peak - 500) < 0.001, "rate peak tracked");
meter.Reset(); meter.Sample(0, t0);
Check(meter.Sample(10, t0.AddSeconds(1)) == 10 && meter.Peak == 10, "rate meter reset");
Check(Signal.Bars(-50) == "\u2582\u2584\u2586\u2588" && Signal.Bars(-65).Trim().Length == 3 && Signal.Bars(-75).Trim().Length == 2 && Signal.Bars(-95).Trim().Length == 1, "signal bars");

// ---- raw log ----
string rawDir = Path.Combine(Path.GetTempPath(), "bleterm_raw_" + Guid.NewGuid().ToString("N"));
string rawPath;
using (var raw = RawLog.Open(rawDir))
{
    rawPath = raw.FilePath;
    raw.Write("RX", new byte[] { 0x01, 0xAB });
    raw.Write("TX", new byte[] { 0x4F });
}
var rawLines = File.ReadAllLines(rawPath);
Check(rawPath.EndsWith("_raw.log") && rawLines.Length == 2 && rawLines[0].EndsWith("RX [2] 01 AB") && rawLines[1].EndsWith("TX [1] 4F"), "raw log format: " + string.Join("|", rawLines));
Directory.Delete(rawDir, true);

// ---- import / export / copy / saved profiles ----
string cfgDir = Path.Combine(Path.GetTempPath(), "bleterm_cfg_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cfgDir);
var cfg = AppSettings.Load(Path.Combine(cfgDir, "none.json"));
cfg.View = ViewMode.Both; cfg.RawLog = true; cfg.RepeatIntervalMs = 250; cfg.DarkMode = true;
cfg.SavedProfiles.Add(new SavedProfile { Name = "Sensor board", Service = "FFE0", Notify = "FFE1", Write = "FFE1" });
cfg.SavedProfiles.Add(new SavedProfile { Name = "", Service = "FFE0" });   // invalid entry is dropped on load
cfg.Macros[0] = new Macro { Name = "OTA", Command = "OTA", AppendNewline = false };
string exp = Path.Combine(cfgDir, "export.json");
cfg.Export(exp);
var back = AppSettings.LoadStrict(exp);
Check(back.View == ViewMode.Both && back.RawLog && back.RepeatIntervalMs == 250 && back.DarkMode, "export/import keeps settings");
Check(back.SavedProfiles.Count == 1 && back.SavedProfiles[0].Name == "Sensor board" && back.Macros[0].Command == "OTA" && !back.Macros[0].AppendNewline, "export/import keeps profiles + macros, drops invalid profile");
var target = new AppSettings();
target.CopyFrom(back);
Check(target.View == ViewMode.Both && target.Macros[0].Name == "OTA" && target.SavedProfiles.Count == 1, "CopyFrom copies all properties");
File.WriteAllText(Path.Combine(cfgDir, "bad1.json"), "{ \"hello\": 1 }");
File.WriteAllText(Path.Combine(cfgDir, "bad2.json"), "[1,2,3]");
File.WriteAllText(Path.Combine(cfgDir, "bad3.json"), "not json");
int rejected = 0;
foreach (var f in new[] { "bad1.json", "bad2.json", "bad3.json" })
{
    try { AppSettings.LoadStrict(Path.Combine(cfgDir, f)); } catch (Exception) { rejected++; }
}
Check(rejected == 3, "import rejects files that are not a configuration");
Directory.Delete(cfgDir, true);

// ---- OTA protocol ----
Check(OtaProtocol.BuildCommand(OtaProtocol.CmdStartApplication, 0x08007000).SequenceEqual(new byte[] { 0x02, 0x00, 0x70, 0x00 }), "ota start cmd @0x08007000 = 02 00 70 00");
Check(OtaProtocol.BuildCommand(OtaProtocol.CmdUploadFinished, 0x0800B800).SequenceEqual(new byte[] { 0x07, 0x00, 0xB8, 0x00 }), "ota finish cmd @0x0800B800 = 07 00 B8 00");
Check(OtaProtocol.TryParseAddress("0x08007000", out var a1) && a1 == 0x08007000, "ota addr 0x-prefixed");
Check(OtaProtocol.TryParseAddress("8007000", out var a2) && a2 == 0x08007000, "ota addr bare hex");
Check(!OtaProtocol.TryParseAddress("zz", out _) && !OtaProtocol.TryParseAddress("", out _), "ota addr invalid");

byte[] Img(uint sp, uint reset, int len)
{
    var b = new byte[len];
    BitConverter.GetBytes(sp).CopyTo(b, 0);
    BitConverter.GetBytes(reset).CopyTo(b, 4);
    return b;
}
var ok = OtaImage.Check(Img(0x20010000, 0x08007101, 4096), 0x08007000);
Check(ok.Error == null && ok.Warnings.Count == 0, "ota image good: " + string.Join("|", ok.Warnings));
var wrongLink = OtaImage.Check(Img(0x20010000, 0x08000101, 4096), 0x08007000);
Check(wrongLink.Error == null && wrongLink.Warnings.Any(w => w.Contains("outside")), "ota image linked for another address");
Check(OtaImage.Check(Encoding.ASCII.GetBytes(":10000000"), 0x08007000).Error != null, "ota rejects Intel HEX");
Check(OtaImage.Check(Img(0x20010000, 0x08007101, 64), 0x80007000).Error != null, "ota rejects 0x80007000");
Check(OtaImage.Check(Img(0x20010000, 0x08007101, 64), 0x08007004).Error != null, "ota rejects non-8-aligned");
Check(OtaImage.Check(Img(0x20010000, 0x08007801, 4096), 0x08007800).Warnings.Any(w => w.Contains("4 KB")), "ota warns non-4KB-aligned");
Check(OtaImage.Check(Array.Empty<byte>(), 0x08007000).Error != null, "ota rejects empty file");

Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;
