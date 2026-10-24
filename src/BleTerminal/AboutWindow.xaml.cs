using System.Reflection;
using System.Windows;
using System.Windows.Documents;

namespace BleTerminal;

/// <summary>About box with author info and the optional donation link.</summary>
public partial class AboutWindow : Window
{
    const string GitHubUrl = "https://github.com/DineshKumargethub";
    const string DonateUrl = "https://buymeacoffee.com/dineshkumarg";

    public AboutWindow()
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        txtVersion.Text = v == null ? "" : "Version " + v.ToString(3);

        txtAbout.Inlines.Add("Created and maintained by ");
        txtAbout.Inlines.Add(new Run("Dineshkumar G.") { FontWeight = FontWeights.SemiBold });
        txtAbout.Inlines.Add(" I am a passionate developer who enjoys building practical software to solve " +
                             "everyday problems and improve usability.");
        txtLicense.Text = "MIT License \u00A9 2026 Dineshkumar G.";
    }

    void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);
    void Donate_Click(object sender, RoutedEventArgs e) => OpenUrl(DonateUrl);

    static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            try { Clipboard.SetText(url); } catch { }
            MessageBox.Show("Could not open the browser. The link was copied to the clipboard:\n" + url,
                "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
