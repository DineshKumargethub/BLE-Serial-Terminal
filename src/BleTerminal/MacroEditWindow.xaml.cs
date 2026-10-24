using System.Windows;
using BleTerminal.Core;

namespace BleTerminal;

public partial class MacroEditWindow : Window
{
    public Macro Result { get; private set; }

    public MacroEditWindow(Macro macro, int number)
    {
        InitializeComponent();
        Title = $"Edit macro M{number}";
        Result = macro.Clone();
        txtName.Text = macro.Name;
        txtCommand.Text = macro.Command;
        chkHex.IsChecked = macro.IsHex;
        chkNewline.IsChecked = macro.AppendNewline;
        txtCommand.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        bool hex = chkHex.IsChecked == true;
        if (hex && !TextCodec.TryParseHex(txtCommand.Text, out _, out var err))
        {
            MessageBox.Show(this, "Invalid hex: " + err, "Edit macro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new Macro
        {
            Name = txtName.Text.Trim(),
            Command = txtCommand.Text,
            IsHex = hex,
            AppendNewline = chkNewline.IsChecked == true
        };
        DialogResult = true;
    }
}
