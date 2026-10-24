using BleTerminal.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BleTerminal;

/// <summary>One BLE UART-style connection: GATT discovery, notify subscription, MTU-sized chunked writes.</summary>
public sealed partial class BleClient
{
    sealed class ServiceInfo
    {
        public ServiceInfo(GattDeviceService svc) { Svc = svc; }
        public GattDeviceService Svc { get; }
        public List<GattCharacteristic> Chars { get; } = new();
    }

    BluetoothLEDevice? _dev;
    GattSession? _session;
    readonly List<ServiceInfo> _services = new();
    GattCharacteristic? _notify;
    GattCharacteristic? _write;
    GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;
    bool _closing;
    readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Raised on a thread-pool thread for every notification.</summary>
    public event Action<byte[]>? DataReceived;
    /// <summary>Human-readable progress / diagnostics (thread-pool thread).</summary>
    public event Action<string>? Info;
    /// <summary>The link dropped without us asking for it.</summary>
    public event Action? ConnectionLost;

    public bool CanWrite => _write != null;

    /// <summary>What the current connection ended up using (profile, service, notify, write, write mode); null when not connected.</summary>
    public ChannelInfo? Channels { get; private set; }

    /// <summary>Bytes per write at the currently negotiated MTU (20 until the session reports a larger one).</summary>
    public int PayloadSize => _session != null ? Math.Max(20, _session.MaxPduSize - 3) : 20;

    /// <summary>Reads the selected device's GATT UUIDs without enabling notifications or starting a terminal session.</summary>
    public async Task<IReadOnlyList<GattServiceOption>> DiscoverServicesAsync(ulong address)
    {
        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException("Could not open the device. Make sure it is still available.");
        var result = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT service discovery failed: {result.Status}. {Hint(result.Status)}");

        var services = new List<GattServiceOption>();
        foreach (var svc in result.Services)
        {
            try
            {
                var chars = new List<GattCharacteristicOption>();
                var charResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charResult.Status == GattCommunicationStatus.Success)
                    chars.AddRange(charResult.Characteristics.Select(c => new GattCharacteristicOption(c.Uuid, c.CharacteristicProperties)));
                services.Add(new GattServiceOption(svc.Uuid, chars));
            }
            catch
            {
                services.Add(new GattServiceOption(svc.Uuid, Array.Empty<GattCharacteristicOption>()));
            }
            finally { svc.Dispose(); }
        }
        return services;
    }

    public async Task ConnectAsync(ulong address, GattProfile profile, WriteMode writeMode, CancellationToken ct = default)
    {
        await CleanupAsync();
        _otaRebootExpected = false;

        var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        if (dev == null) throw new InvalidOperationException("Could not open the device (is it still advertising?).");
        _dev = dev;
        dev.ConnectionStatusChanged += OnConnectionStatusChanged;

        var svcRes = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcRes.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Service discovery failed: {svcRes.Status}. {Hint(svcRes.Status)}");

        foreach (var svc in svcRes.Services)
        {
            var info = new ServiceInfo(svc);
            try
            {
                var chRes = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                if (chRes.Status == GattCommunicationStatus.Success) info.Chars.AddRange(chRes.Characteristics);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* protected services (HID etc.) can refuse; ignore */ }
            _services.Add(info);
        }

        // ---- choose service / profile ----
        ServiceInfo? svcInfo = null;
        GattProfile? used = null;
        if (profile.IsAuto)
        {
            foreach (var p in Profiles.Known)
            {
                svcInfo = _services.FirstOrDefault(s => s.Svc.Uuid == p.Service && IsUsable(s));
                if (svcInfo != null) { used = p; break; }
            }
        }
        else if (profile.Service is Guid sg)
        {
            svcInfo = _services.FirstOrDefault(s => s.Svc.Uuid == sg);
            used = profile;
        }

        if (svcInfo == null || used == null)
        {
            // ST OTA loader (FE22/FE23/FE24): no UART service needed, the Firmware update panel takes over.
            if (profile.IsAuto && HasOtaCharacteristics)
            {
                await KeepAliveAsync(dev, ct);
                Channels = new ChannelInfo("ST OTA loader", null, OtaProtocol.Confirm, OtaProtocol.BaseAddress, "-", true);
                Info?.Invoke("ST OTA service detected (FE22 / FE23 / FE24) - use the Firmware update panel. No terminal channel on this connection.");
                return;
            }

            Info?.Invoke("Services exposed by this device:");
            foreach (var s in _services)
            {
                Info?.Invoke($"  service {s.Svc.Uuid}");
                foreach (var c in s.Chars) Info?.Invoke($"    char {c.Uuid}  [{c.CharacteristicProperties}]");
            }
            throw new InvalidOperationException(profile.IsAuto
                ? "No known UART service found. Pick \"Custom UUIDs\" and enter the service/characteristic UUIDs listed above."
                : $"Service {profile.Service} not found on this device (see the list above).");
        }

        // ---- choose characteristics (an explicit UUID may live in a different service than the chosen one) ----
        var notify = FindByUuid(svcInfo, used.Notify)
            ?? Pick(svcInfo.Chars, null,
                p => p.HasFlag(GattCharacteristicProperties.Notify) || p.HasFlag(GattCharacteristicProperties.Indicate));
        var write = FindByUuid(svcInfo, used.Write)
            ?? Pick(svcInfo.Chars, null,
                p => p.HasFlag(GattCharacteristicProperties.Write) || p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));

        if (notify == null && write == null)
            throw new InvalidOperationException(
                $"Service {svcInfo.Svc.Uuid} has no notify/indicate or writable characteristic. Pick the UUIDs explicitly (they can belong to different services).");

        // ---- subscribe (optional: a write-only command channel is fine) ----
        if (notify != null)
        {
            _notify = notify;
            notify.ValueChanged += OnValueChanged;
            var cccd = notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            var st = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(cccd).AsTask(ct);
            if (st != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Could not enable notifications: {st}. {Hint(st)}");
        }
        else
        {
            Info?.Invoke("No notify/indicate characteristic selected - transmit only.");
        }

        _write = write;
        // Auto prefers a write COMMAND (without response) when the characteristic supports it, like nRF Connect's "Write command".
        bool canResp = write != null && write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        bool canNoResp = write != null && write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        var wanted = writeMode switch
        {
            WriteMode.WithResponse => GattWriteOption.WriteWithResponse,
            WriteMode.WithoutResponse => GattWriteOption.WriteWithoutResponse,
            _ => canNoResp ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse
        };
        if (write != null)
        {
            // a forced mode the characteristic does not support would fail on the first write: fall back and say so
            if (wanted == GattWriteOption.WriteWithoutResponse && !canNoResp && canResp)
            {
                Info?.Invoke("The write characteristic does not support write-without-response; using write-with-response instead.");
                wanted = GattWriteOption.WriteWithResponse;
            }
            else if (wanted == GattWriteOption.WriteWithResponse && !canResp && canNoResp)
            {
                Info?.Invoke("The write characteristic does not support write-with-response; using write-without-response instead.");
                wanted = GattWriteOption.WriteWithoutResponse;
            }
            else if (!canResp && !canNoResp)
            {
                Info?.Invoke("Warning: the selected write characteristic reports no write property; sending will probably fail.");
            }
        }
        _writeOption = wanted;

        // ---- keep link alive + MTU info ----
        await KeepAliveAsync(dev, ct);

        Channels = new ChannelInfo(used.Name, svcInfo.Svc.Uuid, notify?.Uuid, write?.Uuid, _writeOption.ToString(), false);
        Info?.Invoke($"Using {used.Name}  (" + (notify != null ? $"notify {notify.Uuid}" : "no notify") +
                     (write != null ? $", write {write.Uuid}, {_writeOption}" : ", no write characteristic - receive only") + ")");
    }

    async Task KeepAliveAsync(BluetoothLEDevice dev, CancellationToken ct)
    {
        try
        {
            var session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId).AsTask(ct);
            session.MaintainConnection = true;
            _session = session;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* chunk size falls back to 20 */ }
    }

    public async Task SendAsync(byte[] data)
    {
        var ch = _write ?? throw new InvalidOperationException("This service has no writable characteristic.");
        await _sendLock.WaitAsync();
        try
        {
            for (int off = 0; off < data.Length;)
            {
                int chunk = _session != null ? Math.Max(20, _session.MaxPduSize - 3) : 20;
                int n = Math.Min(chunk, data.Length - off);
                var w = new DataWriter();
                w.WriteBytes(data.AsSpan(off, n).ToArray());
                var st = await ch.WriteValueAsync(w.DetachBuffer(), _writeOption);
                if (st != GattCommunicationStatus.Success)
                    throw new IOException($"Write failed: {st}");
                off += n;
            }
        }
        finally { _sendLock.Release(); }
    }

    public Task DisconnectAsync() => CleanupAsync();

    // ------------------------------------------------------------------

    /// <summary>Explicit UUID lookup: chosen service first, then every other service on the device.</summary>
    GattCharacteristic? FindByUuid(ServiceInfo preferred, Guid? uuid)
    {
        if (uuid is not Guid g) return null;
        return preferred.Chars.FirstOrDefault(c => c.Uuid == g)
            ?? _services.SelectMany(s => s.Chars).FirstOrDefault(c => c.Uuid == g);
    }

    /// <summary>A service is only a UART candidate if at least one of its characteristics can notify/indicate or be written.</summary>
    static bool IsUsable(ServiceInfo s) => s.Chars.Any(c =>
        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate) ||
        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));

    static GattCharacteristic? Pick(IEnumerable<GattCharacteristic> chars, Guid? preferred, Func<GattCharacteristicProperties, bool> capable)
    {
        if (preferred is Guid g)
        {
            var m = chars.FirstOrDefault(c => c.Uuid == g);
            if (m != null) return m;
        }
        return chars.FirstOrDefault(c => capable(c.CharacteristicProperties));
    }

    static string Hint(GattCommunicationStatus s) => s switch
    {
        GattCommunicationStatus.Unreachable => "Device unreachable: out of range, off, or already connected to another device (phone?).",
        GattCommunicationStatus.AccessDenied => "Access denied: pair the device in Windows Settings > Bluetooth & devices first.",
        _ => ""
    };

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        if (sender.Uuid == OtaProtocol.Confirm) _otaConfirm?.TrySetResult(data);
        var n = _notify;
        if (n != null && sender.Uuid == n.Uuid) DataReceived?.Invoke(data);
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!_closing && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _otaConfirm?.TrySetResult(Array.Empty<byte>());   // OTA: the device rebooted
            ConnectionLost?.Invoke();
        }
    }

    async Task CleanupAsync()
    {
        _closing = true;
        try
        {
            var notify = _notify;
            _notify = null;
            _write = null;
            Channels = null;
            if (notify != null)
            {
                notify.ValueChanged -= OnValueChanged;
                try
                {
                    var t = notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask();
                    await Task.WhenAny(t, Task.Delay(1500));
                }
                catch { }
            }
            foreach (var s in _services) { try { s.Svc.Dispose(); } catch { } }
            _services.Clear();
            try { _session?.Dispose(); } catch { }
            _session = null;
            if (_dev != null)
            {
                _dev.ConnectionStatusChanged -= OnConnectionStatusChanged;
                try { _dev.Dispose(); } catch { }
                _dev = null;
            }
        }
        finally { _closing = false; }
    }
}


/// <summary>The channels a connection is actually using (shown in the UI and re-reported after a reconnect).</summary>
public sealed record ChannelInfo(string Profile, Guid? Service, Guid? Notify, Guid? Write, string WriteMode, bool OtaOnly);

/// <summary>A discovered GATT service and its characteristic metadata for UUID selection.</summary>
public sealed class GattServiceOption
{
    public GattServiceOption(Guid uuid, IReadOnlyList<GattCharacteristicOption> characteristics)
    {
        Uuid = uuid;
        Characteristics = characteristics;
        Display = uuid.ToString();
    }

    public Guid Uuid { get; }
    public string Display { get; }
    public IReadOnlyList<GattCharacteristicOption> Characteristics { get; }
}

/// <summary>A discovered GATT characteristic and its supported operations.</summary>
public sealed class GattCharacteristicOption
{
    public GattCharacteristicOption(Guid uuid, GattCharacteristicProperties properties)
    {
        Uuid = uuid;
        Properties = properties;
        Display = $"{uuid}  [{properties}]";
    }

    public Guid Uuid { get; }
    public GattCharacteristicProperties Properties { get; }
    public string Display { get; }
    public bool CanNotify => Properties.HasFlag(GattCharacteristicProperties.Notify) || Properties.HasFlag(GattCharacteristicProperties.Indicate);
    public bool CanWrite => Properties.HasFlag(GattCharacteristicProperties.Write) || Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
}
