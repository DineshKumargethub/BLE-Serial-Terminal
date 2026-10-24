using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BleTerminal.Core;
using Microsoft.Win32;

namespace BleTerminal;

public partial class MainWindow : Window
{
    enum ConnState { Idle, Connecting, Connected, Reconnecting }

    readonly AppSettings _s = AppSettings.Load();
    readonly BleScanner _scanner = new();
    readonly BleClient _ble = new();
    readonly ObservableCollection<DeviceItem> _devices = new();
    readonly Dictionary<ulong, DeviceItem> _byAddr = new();
    readonly ICollectionView _deviceView;
    readonly ConcurrentQueue<byte[]> _rxQueue = new();
    readonly RxTextDecoder _rxText = new();
    readonly TerminalView _term;
    readonly List<string> _history = new();
    readonly DispatcherTimer _scanTimer;
    readonly DispatcherTimer _rxTimer;

    ConnState _state = ConnState.Idle;
    int _histIdx;
    ulong _lastAddress;
    GattProfile? _lastProfile;
    string _deviceTitle = "";
    CancellationTokenSource? _reconnectCts;
    CancellationTokenSource? _connectCts;
    SessionLog? _log;
    long _rxBytes, _txBytes;
    IReadOnlyList<GattServiceOption> _discoveredServices = Array.Empty<GattServiceOption>();

    public MainWindow()
    {
        InitializeComponent();

        _term = new TerminalView(rtbTerminal)
        {
            Timestamps = _s.Timestamps,
            AutoScroll = _s.AutoScroll
        };
        rtbTerminal.FontSize = _s.FontSize;
        _rxText.Normalizer.Mode = _s.RxNl;

        // ---- device list ----
        lstDevices.ItemsSource = _devices;
        _deviceView = CollectionViewSource.GetDefaultView(_devices);
        _deviceView.Filter = o => !_s.HideUnnamed || (o is DeviceItem d && d.Name.Length > 0);
        _deviceView.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.SortRssi), ListSortDirection.Descending));
        if (_deviceView is ICollectionViewLiveShaping live)
        {
            if (live.CanChangeLiveFiltering)
            {
                live.LiveFilteringProperties.Add(nameof(DeviceItem.Name));
                live.IsLiveFiltering = true;
            }
            if (live.CanChangeLiveSorting)
            {
                live.LiveSortingProperties.Add(nameof(DeviceItem.SortRssi));
                live.IsLiveSorting = true;
            }
        }

        // ---- settings -> controls ----
        FillEnum(cmbView, _s.View, v => { _s.View = v; _term.CloseLine(); _rxText.Reset(); _s.Save(); });
        FillEnum(cmbRxNl, _s.RxNl, v => { _s.RxNl = v; _rxText.Normalizer.Mode = v; _s.Save(); });
        FillEnum(cmbTxNl, _s.TxNl, v => { _s.TxNl = v; _s.Save(); });
        FillEnum(cmbSend, _s.Send, v => { _s.Send = v; _s.Save(); });
        FillEnum(cmbWriteMode, _s.WriteOpt, v => { _s.WriteOpt = v; _s.Save(); });

        BindCheck(chkEcho, _s.LocalEcho, v => _s.LocalEcho = v);
        BindCheck(chkTimestamps, _s.Timestamps, v => { _s.Timestamps = v; _term.Timestamps = v; });
        BindCheck(chkAutoScroll, _s.AutoScroll, v => { _s.AutoScroll = v; _term.AutoScroll = v; });
        BindCheck(chkAutoLog, _s.AutoLog, v => _s.AutoLog = v);
        BindCheck(chkAutoReconnect, _s.AutoReconnect, v => _s.AutoReconnect = v);
        BindCheck(chkHideUnnamed, _s.HideUnnamed, v => { _s.HideUnnamed = v; _deviceView.Refresh(); UpdateScanHint(); });
        BindCheck(chkDark, _s.DarkMode, v => { _s.DarkMode = v; App.ApplyTheme(v); });

        cmbProfile.SelectionChanged += (_, _) =>
        {
            if (cmbProfile.SelectedItem is GattProfile p)
            {
                _s.Profile = p.Name;
                pnlCustom.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
                UpdateProfileButtons();
                _s.Save();
            }
        };
        RefreshProfileList();
        cmbCustomService.Text = _s.CustomService;
        cmbCustomNotify.Text = _s.CustomNotify;
        cmbCustomWrite.Text = _s.CustomWrite;
        cmbCustomService.SelectionChanged += (_, _) => UpdateCharacteristicChoices();
        cmbCustomService.LostFocus += (_, _) => SaveCustomUuidSettings();
        cmbCustomNotify.LostFocus += (_, _) => SaveCustomUuidSettings();
        cmbCustomWrite.LostFocus += (_, _) => SaveCustomUuidSettings();

        BuildMacroButtons();
        InitOta();
        InitFeatures();

        // ---- events ----
        btnScan.Click += (_, _) => { if (_scanner.IsRunning) StopScan(); else StartScan(); };
        btnConnect.Click += async (_, _) => await OnConnectClickAsync();
        btnDiscoverUuids.Click += async (_, _) => await DiscoverUuidsAsync();
        btnSend.Click += async (_, _) => await SendFromInputAsync();
        btnSave.Click += (_, _) => SaveTerminalText();
        btnLogFolder.Click += (_, _) => ChooseLogFolder();
        txtInput.PreviewKeyDown += async (_, e) => await OnInputKeyAsync(e);
        rtbTerminal.PreviewMouseWheel += OnTerminalWheel;

        _scanner.Failed += msg => Dispatcher.InvokeAsync(() => { StopScan(); Print(msg + "\n", LineKind.Error); });
        _ble.DataReceived += data => _rxQueue.Enqueue(data);
        _ble.Info += msg => Dispatcher.InvokeAsync(() => PrintInfo(msg));
        _ble.ConnectionLost += () => Dispatcher.InvokeAsync(OnConnectionLost);

        _scanTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
        _scanTimer.Tick += (_, _) => SyncDevices();
        _scanTimer.Start();

        _rxTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
        _rxTimer.Tick += (_, _) => DrainRx();
        _rxTimer.Start();

        SetState(ConnState.Idle);
        UpdateCounters();
        PrintInfo("Press Scan, pick a device, then Connect.");
    }

    // =====================================================================
    //  small UI helpers
    // =====================================================================

    static void FillEnum<T>(ComboBox cb, T selected, Action<T> onChange) where T : struct, Enum
    {
        cb.ItemsSource = Enum.GetValues<T>();
        cb.SelectedItem = selected;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is T v) onChange(v); };
    }

    void BindCheck(CheckBox cb, bool initial, Action<bool> onChange)
    {
        cb.IsChecked = initial;
        cb.Click += (_, _) => { onChange(cb.IsChecked == true); _s.Save(); };
    }

    void Print(string text, LineKind kind)
    {
        _term.Append(text, kind);
        _log?.Append(kind switch
        {
            LineKind.Rx => "RX",
            LineKind.Tx => "TX",
            LineKind.Error => "!!",
            _ => "--"
        }, text, _s.Timestamps);
    }

    void PrintInfo(string msg) => Print(msg + "\n", LineKind.Info);

    /// <summary>Fresh RX state for a new link: drops half-received UTF-8 / newline state and any stale queued chunks.</summary>
    void ResetRx()
    {
        _rxText.Reset();
        while (_rxQueue.TryDequeue(out _)) { }
    }

    void UpdateCounters() => txtCounters.Text = BuildStatsText();

    void OnTerminalWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        _s.FontSize = Math.Clamp(_s.FontSize + (e.Delta > 0 ? 1 : -1), 8, 40);
        rtbTerminal.FontSize = _s.FontSize;
        e.Handled = true;
    }

    // =====================================================================
    //  scanning
    // =====================================================================

    void StartScan()
    {
        _reselectAddress = (lstDevices.SelectedItem as DeviceItem)?.Address;   // keep the chosen device selected when it shows up again
        _devices.Clear();
        _byAddr.Clear();
        _scanner.Seen.Clear();
        _scanner.Start();
        _scanStartedAt = DateTime.UtcNow;
        _everScanned = true;
        if (_scanner.IsRunning) btnScan.Content = "Stop";
        UpdateScanHint();
    }

    void StopScan()
    {
        _scanner.Stop();
        btnScan.Content = "Scan";
        UpdateScanHint();
    }

    void SyncDevices()
    {
        foreach (var kv in _scanner.Seen)
        {
            var e = kv.Value;
            if (!_byAddr.TryGetValue(e.Address, out var item))
            {
                item = new DeviceItem(e.Address) { Name = e.Name, Rssi = e.Rssi };
                _byAddr[e.Address] = item;
                _devices.Add(item);
                if (_reselectAddress == e.Address)
                {
                    lstDevices.SelectedItem = item;
                    _reselectAddress = null;
                }
            }
            else
            {
                if (e.Name.Length > 0) item.Name = e.Name;
                item.Rssi = e.Rssi;
            }
        }
        UpdateScanHint();
    }

    // =====================================================================
    //  connection state machine
    // =====================================================================

    void SetState(ConnState s)
    {
        _state = s;
        btnConnect.Content = s switch
        {
            ConnState.Idle => "Connect",
            ConnState.Connecting => "Cancel",
            ConnState.Connected => "Disconnect",
            _ => "Stop reconnecting"
        };
        btnConnect.IsEnabled = true;
        bool idle = s == ConnState.Idle;
        lstDevices.IsEnabled = idle;
        btnScan.IsEnabled = idle;
        cmbProfile.IsEnabled = idle;
        pnlCustom.IsEnabled = idle;
        btnDiscoverUuids.IsEnabled = idle;
        cmbWriteMode.IsEnabled = idle;
        btnSend.IsEnabled = s == ConnState.Connected;
        txtStatus.Text = s switch
        {
            ConnState.Idle => "Not connected",
            ConnState.Connecting => "Connecting...",
            ConnState.Connected => "Connected: " + _deviceTitle,
            _ => "Connection lost - reconnecting..."
        };
        UpdateOtaPanel();
        UpdateConnectionFeedback();
    }

    async Task OnConnectClickAsync()
    {
        switch (_state)
        {
            case ConnState.Idle:
                await StartConnectAsync();
                break;
            case ConnState.Connected:
                await UserDisconnectAsync();
                break;
            case ConnState.Connecting:
                _connectCts?.Cancel();
                break;
            case ConnState.Reconnecting:
                _reconnectCts?.Cancel();
                break;
        }
    }

    async Task DiscoverUuidsAsync()
    {
        if (lstDevices.SelectedItem is not DeviceItem dev)
        {
            PrintInfo("Select a BLE device first, then click Discover UUIDs.");
            return;
        }

        btnDiscoverUuids.IsEnabled = false;
        btnDiscoverUuids.Content = "Discovering...";
        try
        {
            _discoveredServices = await _ble.DiscoverServicesAsync(dev.Address);
            cmbCustomService.ItemsSource = _discoveredServices;
            cmbCustomService.DisplayMemberPath = nameof(GattServiceOption.Display);
            var preferred = UuidParser.TryParse(_s.CustomService, out var savedService) ? savedService : Guid.Empty;
            cmbCustomService.SelectedItem = _discoveredServices.FirstOrDefault(s => s.Uuid == preferred)
                ?? _discoveredServices.FirstOrDefault(s => s.Characteristics.Any(c => c.CanNotify) && s.Characteristics.Any(c => c.CanWrite))
                ?? _discoveredServices.FirstOrDefault();
            UpdateCharacteristicChoices();
            PrintInfo($"Discovered {_discoveredServices.Count} GATT service(s). Select the service, Notify and Write UUIDs.");
        }
        catch (Exception ex)
        {
            Print($"UUID discovery failed: {ex.GetType().Name} | 0x{ex.HResult:X8} | {ex.Message}\n", LineKind.Error);
        }
        finally
        {
            btnDiscoverUuids.Content = "Discover UUIDs";
            btnDiscoverUuids.IsEnabled = _state == ConnState.Idle;
        }
    }

    void UpdateCharacteristicChoices()
    {
        if (cmbCustomService.SelectedItem is not GattServiceOption service) return;
        var oldNotify = ReadUuidText(cmbCustomNotify);
        var oldWrite = ReadUuidText(cmbCustomWrite);
        // Selected service first, then custom services, then SIG-standard ones: notify and write may sit in different services.
        var ordered = service.Characteristics
            .Concat(_discoveredServices.Where(s => s != service)
                .OrderBy(s => IsSigUuid(s.Uuid) ? 1 : 0)
                .SelectMany(s => s.Characteristics))
            .ToList();
        var notifyOptions = ordered.Where(c => c.CanNotify).ToList();
        var writeOptions = ordered.Where(c => c.CanWrite).ToList();

        cmbCustomNotify.ItemsSource = notifyOptions;
        cmbCustomNotify.DisplayMemberPath = nameof(GattCharacteristicOption.Display);
        cmbCustomNotify.SelectedItem = notifyOptions.FirstOrDefault(c => c.Uuid.ToString().Equals(oldNotify, StringComparison.OrdinalIgnoreCase))
            ?? notifyOptions.FirstOrDefault();

        cmbCustomWrite.ItemsSource = writeOptions;
        cmbCustomWrite.DisplayMemberPath = nameof(GattCharacteristicOption.Display);
        cmbCustomWrite.SelectedItem = writeOptions.FirstOrDefault(c => c.Uuid.ToString().Equals(oldWrite, StringComparison.OrdinalIgnoreCase))
            ?? writeOptions.FirstOrDefault();
        SaveCustomUuidSettings();
    }

    static bool IsSigUuid(Guid g) =>
        g.ToString().EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);

    static string ReadUuidText(ComboBox combo) => combo.SelectedItem switch
    {
        GattServiceOption s => s.Uuid.ToString(),
        GattCharacteristicOption c => c.Uuid.ToString(),
        _ => combo.Text.Trim()
    };

    void SaveCustomUuidSettings()
    {
        _s.CustomService = ReadUuidText(cmbCustomService);
        _s.CustomNotify = ReadUuidText(cmbCustomNotify);
        _s.CustomWrite = ReadUuidText(cmbCustomWrite);
        _s.Save();
    }

    GattProfile? ResolveProfile()
    {
        if (cmbProfile.SelectedItem is not GattProfile p) return Profiles.Auto;
        if (!p.IsCustom) return p;

        SaveCustomUuidSettings();

        if (!UuidParser.TryParse(_s.CustomService, out var svc))
        {
            MessageBox.Show(this, "Select or enter a valid service UUID (e.g. FFE0 or 6e400001-b5a3-f393-e0a9-e50e24dcca9e).",
                "Custom UUIDs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        Guid? notify = UuidParser.TryParse(_s.CustomNotify, out var n) ? n : null;
        Guid? write = UuidParser.TryParse(_s.CustomWrite, out var w) ? w : null;
        return new GattProfile { Name = "Custom", Service = svc, Notify = notify, Write = write, IsCustom = true };
    }

    async Task StartConnectAsync()
    {
        if (lstDevices.SelectedItem is not DeviceItem dev)
        {
            PrintInfo("Select a device in the list first (press Scan if it is empty).");
            return;
        }
        var profile = ResolveProfile();
        if (profile == null) return;

        StopScan();
        _lastAddress = dev.Address;
        _lastProfile = profile;
        _deviceTitle = $"{dev.Title} ({dev.Mac})";
        ResetStats();
        SetState(ConnState.Connecting);
        PrintInfo($"Connecting to {_deviceTitle}...");
        _connectCts = new CancellationTokenSource();
        try
        {
            await _ble.ConnectAsync(dev.Address, profile, _s.WriteOpt, _connectCts.Token);
            ResetRx();
            SetState(ConnState.Connected);
            StartLog();
            PrintInfo("Connected.");
        }
        catch (OperationCanceledException)
        {
            await _ble.DisconnectAsync();
            SetState(ConnState.Idle);
            PrintInfo("Connection cancelled.");
        }
        catch (Exception ex)
        {
            await _ble.DisconnectAsync();
            SetState(ConnState.Idle);
            Print($"Connection failed: {ex.Message}\n", LineKind.Error);
        }
        finally
        {
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    async Task UserDisconnectAsync()
    {
        await _ble.DisconnectAsync();
        SetState(ConnState.Idle);
        PrintInfo("Disconnected.");
        CloseLog();
    }

    void OnConnectionLost()
    {
        if (_state != ConnState.Connected) return;
        bool otaDrop = _ble.OtaRebootExpected;   // read now: the next connect resets it
        if (otaDrop)
            PrintInfo("Device disconnected after the update (it is rebooting into the new firmware).");
        else
            Print("Connection lost.\n", LineKind.Error);
        _ = HandleLostAsync(otaDrop);
    }

    async Task HandleLostAsync(bool skipReconnect)
    {
        await _ble.DisconnectAsync();
        if (skipReconnect || !_s.AutoReconnect || _lastProfile == null)
        {
            SetState(ConnState.Idle);
            CloseLog();
            return;
        }

        SetState(ConnState.Reconnecting);
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        while (!ct.IsCancellationRequested)
        {
            PrintInfo("Reconnecting in 3 s...");
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                await _ble.ConnectAsync(_lastAddress, _lastProfile, _s.WriteOpt, ct);
                ResetRx();
                SetState(ConnState.Connected);
                PrintInfo("Reconnected. " + RestoredSummary());
                return;
            }
            catch (OperationCanceledException)
            {
                await _ble.DisconnectAsync();
                break;
            }
            catch (Exception ex)
            {
                await _ble.DisconnectAsync();
                Print($"Reconnect failed: {ex.Message}\n", LineKind.Error);
            }
        }
        SetState(ConnState.Idle);
        PrintInfo("Reconnect cancelled.");
        CloseLog();
    }

    // =====================================================================
    //  RX
    // =====================================================================

    void DrainRx()
    {
        if (_rxQueue.IsEmpty) return;
        var chunks = new List<byte[]>();
        while (_rxQueue.TryDequeue(out var c)) chunks.Add(c);
        foreach (var pkt in chunks) CountRx(pkt);

        if (_s.View == ViewMode.Hex || _s.View == ViewMode.Both)
        {
            foreach (var c in chunks)
            {
                _rxBytes += c.Length;
                Print((_s.View == ViewMode.Both ? TextCodec.HexDump(c) : TextCodec.ToHex(c)) + "\n", LineKind.Rx);
            }
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var c in chunks)
            {
                _rxBytes += c.Length;
                sb.Append(_rxText.Decode(c));
            }
            if (sb.Length > 0) Print(sb.ToString(), LineKind.Rx);
        }
        UpdateCounters();
    }

    // =====================================================================
    //  TX
    // =====================================================================

    async Task OnInputKeyAsync(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await SendFromInputAsync();
                break;
            case Key.Up:
                if (_history.Count > 0)
                {
                    _histIdx = Math.Max(0, _histIdx - 1);
                    txtInput.Text = _history[_histIdx];
                    txtInput.CaretIndex = txtInput.Text.Length;
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (_history.Count > 0)
                {
                    _histIdx = Math.Min(_history.Count, _histIdx + 1);
                    txtInput.Text = _histIdx >= _history.Count ? "" : _history[_histIdx];
                    txtInput.CaretIndex = txtInput.Text.Length;
                }
                e.Handled = true;
                break;
        }
    }

    async Task SendFromInputAsync()
    {
        string text = txtInput.Text;
        bool hex = _s.Send == SendMode.Hex;
        if (hex && string.IsNullOrWhiteSpace(text)) return;
        if (_state != ConnState.Connected) { PrintInfo("Not connected."); return; }

        if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text)) _history.Add(text);
        _histIdx = _history.Count;
        txtInput.Clear();
        await SendAsync(text, hex, appendNewline: true);
    }

    /// <summary>Text is UTF-8 + optional TX newline; hex is sent raw (no newline). Returns false when nothing was sent.</summary>
    async Task<bool> SendAsync(string text, bool hex, bool appendNewline)
    {
        if (_state != ConnState.Connected) { PrintInfo("Not connected."); return false; }

        byte[] payload;
        string echo;
        if (hex)
        {
            if (!TextCodec.TryParseHex(text, out payload, out var err))
            {
                Print($"Invalid hex: {err}\n", LineKind.Error);
                return false;
            }
            if (payload.Length == 0) return false;
            echo = TextCodec.ToHex(payload);
        }
        else
        {
            payload = Encoding.UTF8.GetBytes(text);
            echo = text;
            if (appendNewline) payload = payload.Concat(TextCodec.NewlineBytes(_s.TxNl)).ToArray();
        }

        try
        {
            await _ble.SendAsync(payload);
            _raw?.Write("TX", payload);
            if (_s.LocalEcho) Print(echo + "\n", LineKind.Tx);
            _txBytes += payload.Length;
            _txWrites++;
            UpdateCounters();
            return true;
        }
        catch (Exception ex)
        {
            Print($"Send failed: {ex.Message}\n", LineKind.Error);
            return false;
        }
    }

    // =====================================================================
    //  macros
    // =====================================================================

    void BuildMacroButtons()
    {
        pnlMacros.Children.Clear();
        for (int i = 0; i < _s.Macros.Count; i++)
        {
            int idx = i;
            var m = _s.Macros[i];
            var b = new Button
            {
                Content = string.IsNullOrWhiteSpace(m.Name) ? $"M{idx + 1}" : m.Name,
                MinWidth = 58,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 4, 8, 4),
                ToolTip = (string.IsNullOrEmpty(m.Command) ? "(empty)" : m.Command) + "\nRight-click to edit"
            };
            b.Click += async (_, _) =>
            {
                var mm = _s.Macros[idx];
                if (string.IsNullOrEmpty(mm.Command)) EditMacro(idx);
                else await SendAsync(mm.Command, mm.IsHex, mm.AppendNewline);
            };
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit..." };
            edit.Click += (_, _) => EditMacro(idx);
            menu.Items.Add(edit);
            b.ContextMenu = menu;
            pnlMacros.Children.Add(b);
        }
    }

    void EditMacro(int idx)
    {
        var dlg = new MacroEditWindow(_s.Macros[idx], idx + 1) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _s.Macros[idx] = dlg.Result;
            _s.Save();
            BuildMacroButtons();
        }
    }

    // =====================================================================
    //  logging / saving
    // =====================================================================

    static string DefaultLogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BleTerminal", "Logs");

    void StartLog()
    {
        CloseLog();
        if (_s.AutoLog)
        {
            try
            {
                string folder = string.IsNullOrWhiteSpace(_s.LogFolder) ? DefaultLogFolder : _s.LogFolder;
                _log = SessionLog.Open(folder);
                PrintInfo("Logging to " + _log.FilePath);
            }
            catch (Exception ex)
            {
                _log = null;
                Print($"Cannot open log file: {ex.Message}\n", LineKind.Error);
            }
        }
        if (_s.RawLog) OpenRawLog();
    }

    void CloseLog()
    {
        _log?.Dispose();
        _log = null;
        _raw?.Dispose();
        _raw = null;
    }

    void ChooseLogFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Folder for log files",
            InitialDirectory = string.IsNullOrWhiteSpace(_s.LogFolder) ? DefaultLogFolder : _s.LogFolder
        };
        if (dlg.ShowDialog(this) == true)
        {
            _s.LogFolder = dlg.FolderName;
            _s.Save();
            PrintInfo("Log folder: " + _s.LogFolder);
        }
    }

    void SaveTerminalText()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"terminal_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _term.GetText()); }
        catch (Exception ex) { Print($"Save failed: {ex.Message}\n", LineKind.Error); }
    }

    // =====================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanner.Stop();
        _reconnectCts?.Cancel();
        _repeatCts?.Cancel();
        CloseLog();
        if (!_skipSaveOnClose) _s.Save();
        try { Task.Run(() => _ble.DisconnectAsync()).Wait(2000); } catch { }
        base.OnClosing(e);
    }
}
