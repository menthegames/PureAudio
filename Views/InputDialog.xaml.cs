using System.Windows;
using System.Windows.Input;

namespace PureAudio.Views;

public partial class InputDialog : Window
{
    public string? InputText { get; private set; }

    /// <summary>
    /// Whether the checkbox was checked (only relevant when checkbox is shown).
    /// </summary>
    public bool IsCheckBoxChecked => WarningCheckBox.IsChecked == true;

    /// <summary>
    /// Creates an InputDialog for text input (playlist naming, etc.).
    /// </summary>
    public InputDialog(string? initialText = null, string title = "Enter playlist name:")
    {
        InitializeComponent();
        TitleText.Text = title;
        NameTextBox.Text = initialText ?? "";
        NameTextBox.Visibility = Visibility.Visible;
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    /// <summary>
    /// Creates an InputDialog for showing a message with an optional checkbox.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message text to display.</param>
    /// <param name="checkboxText">If not null/empty, shows a checkbox with this text.</param>
    public InputDialog(string title, string message, string? checkboxText = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
        NameTextBox.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(checkboxText))
        {
            WarningCheckBox.Content = checkboxText;
            WarningCheckBox.Visibility = Visibility.Visible;
        }

        OkButton.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // If the text box is visible, validate input
        if (NameTextBox.Visibility == Visibility.Visible)
        {
            InputText = NameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(InputText))
            {
                NameTextBox.Focus();
                return;
            }
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
