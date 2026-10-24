using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace BleTerminal;

public sealed class ScanEntry
{
    public ulong Address;
    public string Name = "";
    public int Rssi;
}

/// <summary>Active BLE advertisement scan. Results accumulate in <see cref="Seen"/>; the UI polls it on a timer.</summary>
public sealed class BleScanner
{
    BluetoothLEAdvertisementWatcher? _watcher;

    public ConcurrentDictionary<ulong, ScanEntry> Seen { get; } = new();
    public event Action<string>? Failed;
    public bool IsRunning => _watcher != null;

    public void Start()
    {
        Stop();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += OnReceived;
        w.Stopped += OnStopped;
        _watcher = w;
        try { w.Start(); }
        catch (Exception ex)
        {
            _watcher = null;
            Failed?.Invoke("Could not start scan: " + ex.Message);
        }
    }

    public void Stop()
    {
        var w = _watcher;
        _watcher = null;
        if (w == null) return;
        w.Received -= OnReceived;
        w.Stopped -= OnStopped;
        try { w.Stop(); } catch { }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        string name = e.Advertisement.LocalName ?? "";
        int rssi = e.RawSignalStrengthInDBm;
        ulong addr = e.BluetoothAddress;
        Seen.AddOrUpdate(addr,
            _ => new ScanEntry { Address = addr, Name = name, Rssi = rssi },
            (_, old) =>
            {
                if (name.Length > 0) old.Name = name;   // name may only be in the scan response
                old.Rssi = rssi;
                return old;
            });
    }

    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
    {
        if (e.Error == BluetoothError.Success) return;
        if (ReferenceEquals(_watcher, sender)) _watcher = null;
        string hint = e.Error == BluetoothError.RadioNotAvailable ? " (is Bluetooth switched on?)" : "";
        Failed?.Invoke($"Scan stopped: {e.Error}{hint}");
    }
}
