using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BleTerminal.Core;
using Microsoft.Win32;

namespace BleTerminal;

/// <summary>Clear / Config menus, configuration import-export and saved UUID profiles.</summary>
public partial class MainWindow
{
    bool _skipSaveOnClose;

    void InitConfig()
    {
        var clearMenu = new ContextMenu();
        AddMenuItem(clearMenu, "Clear display", () => _term.Clear());
        AddMenuItem(clearMenu, "Reset counters and rates", ResetStats);
        AddMenuItem(clearMenu, "Clear session (display, counters, new log files)", ClearSession);
        btnClear.Click += (_, _) => ShowMenu(btnClear, clearMenu);

        var configMenu = new ContextMenu();
        AddMenuItem(configMenu, "Export configuration...", ExportConfig);
        AddMenuItem(configMenu, "Import configuration...", ImportConfig);
        btnConfig.Click += (_, _) => ShowMenu(btnConfig, configMenu);

        btnSaveProfile.Click += (_, _) => SaveCurrentAsProfile();
        btnDeleteProfile.Click += (_, _) => DeleteSelectedProfile();
    }

    static void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    static void ShowMenu(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ClearSession()
    {
        _term.Clear();
        ResetStats();
        if (_state == ConnState.Connected) StartLog();   // closes the current log files, opens fresh ones
        PrintInfo("Session cleared.");
    }

    // ---------------------------------------------------------------- saved UUID profiles

    void RefreshProfileList(string? select = null)
    {
        var items = new List<GattProfile>();
        items.AddRange(Profiles.All.Where(p => !p.IsCustom));
        foreach (var sp in _s.SavedProfiles)
        {
            if (!UuidParser.TryParse(sp.Service, out var svc)) continue;
            items.Add(new GattProfile
            {
                Name = sp.Name,
                Service = svc,
                Notify = UuidParser.TryParse(sp.Notify, out var n) ? n : null,
                Write = UuidParser.TryParse(sp.Write, out var w) ? w : null,
                IsSaved = true
            });
        }
        items.Add(Profiles.Custom);

        cmbProfile.ItemsSource = items;
        string want = select ?? _s.Profile;
        cmbProfile.SelectedItem = items.FirstOrDefault(p => p.Name == want) ?? Profiles.Auto;
        UpdateProfileButtons();
    }

    void UpdateProfileButtons() =>
        btnDeleteProfile.Visibility = (cmbProfile.SelectedItem as GattProfile)?.IsSaved == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    void SaveCurrentAsProfile()
    {
        SaveCustomUuidSettings();
        if (!UuidParser.TryParse(_s.CustomService, out _))
        {
            MessageBox.Show(this, "Select or enter a valid service UUID first.", "Save profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new PromptWindow("Save profile", "Name for this UUID profile (e.g. the device family):") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string name = dlg.Value;
        if (Profiles.All.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That name belongs to a built-in profile. Please choose another.", "Save profile",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.SavedProfiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _s.SavedProfiles.Add(new SavedProfile { Name = name, Service = _s.CustomService, Notify = _s.CustomNotify, Write = _s.CustomWrite });
        _s.Save();
        RefreshProfileList(name);
        PrintInfo($"Saved profile \"{name}\".");
    }

    void DeleteSelectedProfile()
    {
        if (cmbProfile.SelectedItem is not GattProfile selected || !selected.IsSaved) return;
        var answer = MessageBox.Show(this, $"Delete the saved profile \"{selected.Name}\"?", "Delete profile",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _s.SavedProfiles.RemoveAll(p => p.Name == selected.Name);
        _s.Save();
        RefreshProfileList(Profiles.Auto.Name);
    }

    // ---------------------------------------------------------------- export / import

    void ExportConfig()
    {
        _s.Save();
        var dlg = new SaveFileDialog
        {
            Filter = "BLE Terminal configuration (*.json)|*.json",
            FileName = "BleTerminal-config.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _s.Export(dlg.FileName);
            PrintInfo("Configuration exported to " + dlg.FileName);
        }
        catch (Exception ex)
        {
            Print($"Export failed: {ex.Message}\n", LineKind.Error);
        }
    }

    void ImportConfig()
    {
        var dlg = new OpenFileDialog { Filter = "BLE Terminal configuration (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;

        AppSettings imported;
        try { imported = AppSettings.LoadStrict(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot import this file: " + ex.Message, "Import configuration",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int macros = imported.Macros.Count(m => !string.IsNullOrEmpty(m.Command));
        var answer = MessageBox.Show(this,
            $"Replace your macros, saved profiles and settings with the ones in this file?\n\n" +
            $"{imported.SavedProfiles.Count} saved profile(s), {macros} macro(s).\nThe app restarts to apply them.",
            "Import configuration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        imported.LogFolder = _s.LogFolder;   // machine-specific paths stay as they are on this PC
        imported.OtaFile = _s.OtaFile;
        _s.CopyFrom(imported);
        _s.Save();
        RestartApp();
    }

    void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Configuration imported. Please restart the app to apply it.", "Import configuration",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _skipSaveOnClose = true;   // the imported file is already saved; do not rewrite it while the new instance reads it
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _skipSaveOnClose = false;
            MessageBox.Show(this, "Configuration imported, but the app could not restart itself: " + ex.Message +
                "\nPlease start it again manually.", "Import configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Application.Current.Shutdown();
    }
}
