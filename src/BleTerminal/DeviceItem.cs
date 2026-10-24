using System.ComponentModel;
using BleTerminal.Core;

namespace BleTerminal;

public sealed class DeviceItem : INotifyPropertyChanged
{
    string _name = "";
    int _rssi;
    int _sortRssi;
    bool _sortInit;

    public DeviceItem(ulong address) { Address = address; }

    public ulong Address { get; }
    public string Mac => TextCodec.ToMac(Address);

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise(nameof(Name));
            Raise(nameof(Title));
        }
    }

    public int Rssi
    {
        get => _rssi;
        set
        {
            if (_rssi == value) return;
            _rssi = value;
            Raise(nameof(RssiText));
            Raise(nameof(Bars));
            // Sort key only moves on a real change (6 dB), so the list does not shuffle with every advertisement.
            if (!_sortInit || Math.Abs(value - _sortRssi) >= 6)
            {
                _sortRssi = value;
                _sortInit = true;
                Raise(nameof(SortRssi));
            }
        }
    }

    public string Title => string.IsNullOrEmpty(_name) ? "(unnamed)" : _name;
    public string RssiText => $"{_rssi} dBm";
    public string Bars => Signal.Bars(_rssi);
    public int SortRssi => _sortRssi;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
