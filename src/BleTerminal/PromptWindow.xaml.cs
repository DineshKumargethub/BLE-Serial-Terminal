using System.Windows;

namespace BleTerminal;

/// <summary>Tiny "enter a name" dialog.</summary>
public partial class PromptWindow : Window
{
    public string Value => txtValue.Text.Trim();

    public PromptWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        txtPrompt.Text = prompt;
        txtValue.Text = initial;
        txtValue.SelectAll();
        txtValue.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }
}
