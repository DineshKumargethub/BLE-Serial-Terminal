using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BleTerminal.Core;

namespace BleTerminal;

/// <summary>Connection feedback, packet/throughput diagnostics, raw byte log and the "nothing found" scan hints.</summary>
public partial class MainWindow
{
    long _rxPackets, _txWrites;
    int _rxMaxPacket;
    double _rxRate, _txRate;
    readonly RateMeter _rxMeter = new();
    readonly RateMeter _txMeter = new();
    DispatcherTimer? _statsTimer;
    RawLog? _raw;
    DateTime _scanStartedAt;
    bool _everScanned;
    ulong? _reselectAddress;

    void InitFeatures()
    {
        InitDiag();
        InitConfig();
        InitRepeat();
        UpdateScanHint();
    }

    void InitDiag()
    {
        BindCheck(chkRawLog, _s.RawLog, v =>
        {
            _s.RawLog = v;
            if (!v) { _raw?.Dispose(); _raw = null; }
            else if (_state == ConnState.Connected && _raw == null) OpenRawLog();
        });

        _statsTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            _rxRate = _rxMeter.Sample(_rxBytes, now);
            _txRate = _txMeter.Sample(_txBytes, now);
            UpdateCounters();
        };
        _statsTimer.Start();
    }

    // ---------------------------------------------------------------- stats / raw log

    void CountRx(byte[] packet)
    {
        _rxPackets++;
        if (packet.Length > _rxMaxPacket) _rxMaxPacket = packet.Length;
        _raw?.Write("RX", packet);
    }

    void ResetStats()
    {
        _rxBytes = _txBytes = 0;
        _rxPackets = _txWrites = 0;
        _rxMaxPacket = 0;
        _rxRate = _txRate = 0;
        _rxMeter.Reset();
        _txMeter.Reset();
        UpdateCounters();
    }

    string BuildStatsText()
    {
        var sb = new StringBuilder();
        sb.Append($"RX {_rxBytes:N0} B  {_rxPackets:N0} pkts  {TextCodec.FormatRate(_rxRate)}");
        sb.Append($"\nTX {_txBytes:N0} B  {_txWrites:N0} writes  {TextCodec.FormatRate(_txRate)}");
        if (_state == ConnState.Connected)
            sb.Append($"\nwrite payload {_ble.PayloadSize} B, largest RX packet {_rxMaxPacket} B, peak RX {TextCodec.FormatRate(_rxMeter.Peak)}");
        return sb.ToString();
    }

    void OpenRawLog()
    {
        try
        {
            string folder = string.IsNullOrWhiteSpace(_s.LogFolder) ? DefaultLogFolder : _s.LogFolder;
            _raw = RawLog.Open(folder);
            PrintInfo("Raw byte log: " + _raw.FilePath);
        }
        catch (Exception ex)
        {
            _raw = null;
            Print($"Cannot open raw log: {ex.Message}\n", LineKind.Error);
        }
    }

    // ---------------------------------------------------------------- connection feedback

    static string UuidOrNone(Guid? g) => g is Guid v ? v.ToString() : "none";

    /// <summary>Called from SetState: state dot, window title, active service/characteristics, button availability.</summary>
    void UpdateConnectionFeedback()
    {
        dotState.Fill = _state switch
        {
            ConnState.Connected => Brushes.LimeGreen,
            ConnState.Connecting => Brushes.Orange,
            ConnState.Reconnecting => Brushes.Gold,
            _ => Brushes.Gray
        };
        Title = _state == ConnState.Connected ? $"BLE Serial Terminal - {_deviceTitle}" : "BLE Serial Terminal";

        var ch = _ble.Channels;
        txtChannels.Text = _state != ConnState.Connected || ch == null
            ? ""
            : ch.OtaOnly
                ? "ST OTA loader (FE22 / FE23 / FE24)"
                : $"{ch.Profile}\nService {UuidOrNone(ch.Service)}\nNotify  {UuidOrNone(ch.Notify)}\nWrite   {UuidOrNone(ch.Write)} ({ch.WriteMode})";

        bool idle = _state == ConnState.Idle;
        btnSaveProfile.IsEnabled = idle;
        btnDeleteProfile.IsEnabled = idle;
        if (_state != ConnState.Connected) _repeatCts?.Cancel();
        btnRepeat.IsEnabled = _state == ConnState.Connected || _repeatCts != null;
        UpdateCounters();
    }

    /// <summary>After a reconnect: what was actually restored (profile, notifications, write channel).</summary>
    string RestoredSummary()
    {
        var ch = _ble.Channels;
        if (ch == null) return "";
        if (ch.OtaOnly) return "Restored: ST OTA loader.";
        return $"Restored: {ch.Profile}; notifications {(ch.Notify != null ? "on" : "none")}; write {(ch.Write != null ? ch.WriteMode : "none")}.";
    }

    // ---------------------------------------------------------------- scan hints

    string HiddenMessage(int n) =>
        $"{n} device(s) found, but all are unnamed and hidden.\nUntick \"Hide unnamed devices\" to see them.";

    void UpdateScanHint()
    {
        int total = _devices.Count;
        int visible = _deviceView.Cast<object>().Count();
        string? msg = null;
        if (_scanner.IsRunning)
        {
            double secs = (DateTime.UtcNow - _scanStartedAt).TotalSeconds;
            if (total == 0)
                msg = secs < 5 ? "Scanning..." : "No devices found.\nCheck that the device is powered and advertising,\nand that Bluetooth is switched on.";
            else if (visible == 0)
                msg = HiddenMessage(total);
        }
        else
        {
            if (total == 0)
                msg = _everScanned ? "No devices found.\nPress Scan to try again." : "Press Scan to look for BLE devices.";
            else if (visible == 0)
                msg = HiddenMessage(total);
        }
        txtScanHint.Text = msg ?? "";
        txtScanHint.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
