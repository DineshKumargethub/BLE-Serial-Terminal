using BleTerminal.Core;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BleTerminal;

/// <summary>ST STM32WB FUOTA uploader: Base-address (FE22), Confirmation (FE23) and Raw-data (FE24) characteristics.</summary>
public sealed partial class BleClient
{
    TaskCompletionSource<byte[]>? _otaConfirm;
    volatile bool _otaRebootExpected;

    /// <summary>True once the OTA "upload finished" command is on its way: a dropped link is then the reboot, not a fault.</summary>
    public bool OtaRebootExpected => _otaRebootExpected;

    GattCharacteristic? FindChar(Guid uuid) =>
        _services.SelectMany(s => s.Chars).FirstOrDefault(c => c.Uuid == uuid);

    /// <summary>True when the connected device exposes the ST OTA characteristics (any service).</summary>
    public bool HasOtaCharacteristics =>
        FindChar(OtaProtocol.BaseAddress) != null &&
        FindChar(OtaProtocol.Confirm) != null &&
        FindChar(OtaProtocol.RawData) != null;

    static async Task WriteToAsync(GattCharacteristic ch, byte[] data)
    {
        var opt = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var w = new DataWriter();
        w.WriteBytes(data);
        var st = await ch.WriteValueAsync(w.DetachBuffer(), opt);
        if (st != GattCommunicationStatus.Success)
            throw new IOException($"Write to {ch.Uuid} failed: {st}");
    }

    /// <summary>
    /// Flashes <paramref name="image"/> at <paramref name="flashAddress"/>:
    /// enable indications on FE23 -> FE22 &lt;- 02 + address -> stream FE24 -> FE22 &lt;- 07 + address -> wait for the confirmation indication.
    /// </summary>
    public async Task OtaUploadAsync(byte[] image, uint flashAddress, int chunkDelayMs,
                                     IProgress<double>? progress, Action<string> log, CancellationToken ct)
    {
        var baseCh = FindChar(OtaProtocol.BaseAddress);
        var confCh = FindChar(OtaProtocol.Confirm);
        var rawCh = FindChar(OtaProtocol.RawData);
        if (baseCh == null || confCh == null || rawCh == null)
            throw new InvalidOperationException("The ST OTA characteristics (FE22 / FE23 / FE24) were not found on this connection.");

        await _sendLock.WaitAsync(ct);
        _otaRebootExpected = false;
        bool subscribedHere = false;
        try
        {
            // 1) The loader only reboots after the client acknowledges its confirmation indication, so indications must be on.
            var sel = _notify;
            if (sel == null || sel.Uuid != confCh.Uuid)
            {
                confCh.ValueChanged += OnValueChanged;
                subscribedHere = true;
                var st = await confCh.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(ct);
                if (st != GattCommunicationStatus.Success)
                    throw new IOException($"Could not enable indications on the confirmation characteristic: {st}. {Hint(st)}");
                log("Indications enabled on the confirmation characteristic (FE23).");
            }

            // 2) start application upload at the chosen address
            var start = OtaProtocol.BuildCommand(OtaProtocol.CmdStartApplication, flashAddress);
            await WriteToAsync(baseCh, start);
            log($"FE22 <- {TextCodec.ToHex(start)}   (start application upload at 0x{flashAddress:X8})");
            await Task.Delay(300, ct);

            // 3) raw data
            int chunk = _session != null ? Math.Max(20, _session.MaxPduSize - 3) : 20;
            chunk = Math.Min(chunk, 240);
            if (chunk >= 32) chunk -= chunk % 16;
            log($"Sending {image.Length:N0} bytes: {chunk} bytes per write, {chunkDelayMs} ms gap.");
            for (int off = 0; off < image.Length; off += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(chunk, image.Length - off);
                await WriteToAsync(rawCh, image.AsSpan(off, n).ToArray());
                progress?.Report((off + n) / (double)image.Length);
                if (chunkDelayMs > 0) await Task.Delay(chunkDelayMs, ct);
            }
            log("All data sent.");

            // 4) finish, then wait for the confirmation indication (the loader flushes the last bytes and reboots after we ACK it)
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _otaConfirm = tcs;
            _otaRebootExpected = true;   // set BEFORE the write: the device may reboot (and the link drop) immediately
            var fin = OtaProtocol.BuildCommand(OtaProtocol.CmdUploadFinished, flashAddress);
            try { await WriteToAsync(baseCh, fin); }
            catch { _otaRebootExpected = false; throw; }
            log($"FE22 <- {TextCodec.ToHex(fin)}   (upload finished)");

            // Success does not depend on the indication: the loader may reboot before it reaches us. Wait briefly for either.
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(5000, ct));
            ct.ThrowIfCancellationRequested();
            if (winner != tcs.Task)
                log("No confirmation or disconnect within 5 s. All data and the finish command were sent; if the device did not restart, power-cycle it.");
            else if (tcs.Task.Result.Length == 0)
                log("Device disconnected - it is rebooting into the new firmware.");
            else
                log($"FE23 -> {TextCodec.ToHex(tcs.Task.Result)}   (confirmation) - the device is rebooting.");
        }
        finally
        {
            _otaConfirm = null;
            if (subscribedHere) confCh.ValueChanged -= OnValueChanged;
            _sendLock.Release();
        }
    }
}
