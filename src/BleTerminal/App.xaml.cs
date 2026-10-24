using System.Windows;
using BleTerminal.Core;

namespace BleTerminal;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "BLE Serial Terminal - unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme(AppSettings.Load().DarkMode);
        base.OnStartup(e);
    }

    /// <summary>Swaps the colour palette (merged dictionary 0); every DynamicResource in the UI follows.</summary>
    public static void ApplyTheme(bool dark)
    {
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
    }
}
