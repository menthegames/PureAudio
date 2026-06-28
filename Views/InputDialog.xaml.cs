using System.Windows;
using System.Windows.Input;

namespace PureAudio.Views;

public partial class InputDialog : Window
{
    public string? InputText { get; private set; }

    public InputDialog(string? initialText = null, string title = "Enter playlist name:")
    {
        InitializeComponent();
        NameTextBox.Text = initialText ?? "";
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        InputText = NameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(InputText))
        {
            NameTextBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OkButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            CancelButton_Click(sender, e);
        }
    }
}
