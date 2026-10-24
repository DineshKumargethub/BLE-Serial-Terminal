using System.Windows;

namespace BleTerminal;

public partial class MainWindow
{
    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
}
