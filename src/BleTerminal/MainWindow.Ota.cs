using System.Windows;
using BleTerminal.Core;
using Microsoft.Win32;

namespace BleTerminal;

/// <summary>Firmware-update panel: appears when the connected device exposes ST's OTA characteristics.</summary>
public partial class MainWindow
{
    CancellationTokenSource? _otaCts;

    void InitOta()
    {
        txtOtaFile.Text = _s.OtaFile;
        txtOtaAddress.Text = string.IsNullOrWhiteSpace(_s.OtaAddress) ? $"0x{OtaProtocol.DefaultAddress:X8}" : _s.OtaAddress;
        txtOtaDelay.Text = _s.OtaDelayMs.ToString();
        btnOtaBrowse.Click += (_, _) => BrowseOtaFile();
        btnOtaStart.Click += async (_, _) => await StartOtaAsync();
        btnOtaCancel.Click += (_, _) => _otaCts?.Cancel();
    }

    void UpdateOtaPanel() =>
        pnlOta.Visibility = _state == ConnState.Connected && _ble.HasOtaCharacteristics
            ? Visibility.Visible
            : Visibility.Collapsed;

    void BrowseOtaFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Firmware binary (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = txtOtaFile.Text
        };
        if (dlg.ShowDialog(this) == true) txtOtaFile.Text = dlg.FileName;
    }

    void SetOtaBusy(bool busy)
    {
        btnOtaStart.IsEnabled = !busy;
        btnOtaCancel.IsEnabled = busy;
        btnOtaBrowse.IsEnabled = !busy;
        txtOtaFile.IsEnabled = !busy;
        txtOtaAddress.IsEnabled = !busy;
        txtOtaDelay.IsEnabled = !busy;
    }

    async Task StartOtaAsync()
    {
        if (_state != ConnState.Connected || !_ble.HasOtaCharacteristics)
        {
            PrintInfo("Not connected to an ST OTA service.");
            return;
        }
        string path = txtOtaFile.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Choose an existing .bin file.", "Firmware update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!OtaProtocol.TryParseAddress(txtOtaAddress.Text, out uint address))
        {
            MessageBox.Show(this, "Flash address must be hex, e.g. 0x08007000.", "Firmware update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(txtOtaDelay.Text, out int delay) || delay < 0 || delay > 1000)
        {
            MessageBox.Show(this, "The gap between writes must be 0-1000 ms.", "Firmware update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        byte[] image;
        try { image = await File.ReadAllBytesAsync(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot read the file: " + ex.Message, "Firmware update", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var check = OtaImage.Check(image, address);
        if (check.Error != null)
        {
            MessageBox.Show(this, check.Error, "Firmware update", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        string warn = check.Warnings.Count == 0 ? "" : "\n\nWarnings:\n- " + string.Join("\n- ", check.Warnings);
        var answer = MessageBox.Show(this,
            $"Flash {Path.GetFileName(path)} ({image.Length:N0} bytes) to 0x{address:X8}?{warn}",
            "Confirm firmware update", MessageBoxButton.YesNo,
            check.Warnings.Count == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _s.OtaFile = path;
        _s.OtaAddress = $"0x{address:X8}";
        _s.OtaDelayMs = delay;
        _s.Save();

        SetOtaBusy(true);
        _otaCts = new CancellationTokenSource();
        barOta.Value = 0;
        var progress = new Progress<double>(p =>
        {
            barOta.Value = p * 100;
            txtOtaStatus.Text = $"Sending... {p:P0}";
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            PrintInfo($"OTA: {Path.GetFileName(path)}, {image.Length:N0} bytes -> 0x{address:X8}");
            await _ble.OtaUploadAsync(image, address, delay, progress, PrintInfo, _otaCts.Token);
            barOta.Value = 100;
            txtOtaStatus.Text = $"Done in {sw.Elapsed.TotalSeconds:F1} s - device is rebooting.";
            PrintInfo("OTA complete. " + txtOtaStatus.Text);
        }
        catch (OperationCanceledException)
        {
            txtOtaStatus.Text = "Cancelled. The device stays in OTA mode - start again to retry.";
            PrintInfo("OTA cancelled.");
        }
        catch (Exception ex)
        {
            txtOtaStatus.Text = "Failed: " + ex.Message;
            Print("OTA failed: " + ex.Message + "\n", LineKind.Error);
        }
        finally
        {
            _otaCts.Dispose();
            _otaCts = null;
            SetOtaBusy(false);
        }
    }
}
