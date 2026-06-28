using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace PureAudio.Views;

public partial class SelectPlaylistDialog : Window
{
    public string? SelectedPlaylistName { get; private set; }

    public SelectPlaylistDialog(IEnumerable<string> playlistNames)
    {
        InitializeComponent();

        foreach (var name in playlistNames)
        {
            PlaylistListBox.Items.Add(name);
        }

        if (PlaylistListBox.Items.Count > 0)
            PlaylistListBox.SelectedIndex = 0;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is string name)
        {
            SelectedPlaylistName = name;
            DialogResult = true;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is string name)
        {
            SelectedPlaylistName = name;
            DialogResult = true;
            Close();
        }
    }
}
