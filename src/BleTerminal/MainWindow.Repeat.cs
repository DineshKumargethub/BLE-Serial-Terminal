using System.Diagnostics;
using System.Windows;
using BleTerminal.Core;

namespace BleTerminal;

/// <summary>Periodic send of the input-box content (separate from one-shot macros).</summary>
public partial class MainWindow
{
    CancellationTokenSource? _repeatCts;

    void InitRepeat()
    {
        txtRepeatMs.Text = _s.RepeatIntervalMs.ToString();
        txtRepeatCount.Text = _s.RepeatCount.ToString();
        btnRepeat.Click += async (_, _) =>
        {
            if (_repeatCts != null) _repeatCts.Cancel();
            else await StartRepeatAsync();
        };
    }

    async Task StartRepeatAsync()
    {
        if (_state != ConnState.Connected) { PrintInfo("Not connected."); return; }

        string text = txtInput.Text;
        bool hex = _s.Send == SendMode.Hex;
        if (hex)
        {
            if (!TextCodec.TryParseHex(text, out var bytes, out var err) || bytes.Length == 0)
            {
                MessageBox.Show(this, "Type valid hex bytes in the input box first" + (err.Length > 0 ? " (" + err + ")" : "") + ".",
                    "Repeat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (text.Length == 0)
        {
            MessageBox.Show(this, "Type the text to repeat in the input box first.", "Repeat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(txtRepeatMs.Text, out int interval) || interval < 10 || interval > 3_600_000)
        {
            MessageBox.Show(this, "Interval must be 10 - 3,600,000 ms.", "Repeat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(txtRepeatCount.Text, out int count) || count < 0)
        {
            MessageBox.Show(this, "Count must be 0 (until stopped) or a positive number.", "Repeat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.RepeatIntervalMs = interval;
        _s.RepeatCount = count;
        _s.Save();

        _repeatCts = new CancellationTokenSource();
        var ct = _repeatCts.Token;
        SetRepeatRunning(true);
        int sent = 0;
        string ending = "stopped";
        var clock = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested && _state == ConnState.Connected)
            {
                if (!await SendAsync(text, hex, appendNewline: true)) { ending = "stopped (send failed)"; break; }
                sent++;
                txtRepeatStatus.Text = count == 0 ? $"Sent {sent}" : $"Sent {sent} / {count}";
                if (count != 0 && sent >= count) { ending = "done"; break; }

                // schedule against a running clock so the interval does not drift with send time
                long wait = (long)interval * sent - clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _repeatCts?.Dispose();
            _repeatCts = null;
            SetRepeatRunning(false);
        }
        if (_state != ConnState.Connected && ending == "stopped") ending = "stopped (disconnected)";
        txtRepeatStatus.Text = $"Sent {sent} - {ending}";
    }

    void SetRepeatRunning(bool running)
    {
        btnRepeat.Content = running ? "Stop repeat" : "Start repeat";
        txtRepeatMs.IsEnabled = !running;
        txtRepeatCount.IsEnabled = !running;
        btnRepeat.IsEnabled = running || _state == ConnState.Connected;
    }
}
