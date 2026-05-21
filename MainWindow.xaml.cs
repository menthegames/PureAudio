using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PureAudio.Controls;
using PureAudio.ViewModels;

namespace PureAudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SpectrumControl _spectrumControl;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Create SpectrumControl programmatically
        _spectrumControl = new SpectrumControl();
        _spectrumControl.SetBinding(SpectrumControl.DataProperty, "FftData");
        _spectrumControl.SetBinding(SpectrumControl.PeakDataProperty, "FftPeakData");
        _spectrumControl.BarColor = System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00);
        _spectrumControl.PeakColor = System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44);
        _spectrumControl.BackgroundColor = System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A);

        // Find FftContainer by name (it's inside a Border template)
        if (FindName("FftContainer") is Grid fftContainer)
        {
            fftContainer.Children.Add(_spectrumControl);
            System.Diagnostics.Debug.WriteLine("MainWindow: SpectrumControl added to FftContainer via FindName");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("MainWindow: ERROR - FftContainer not found!");
        }

        System.Diagnostics.Debug.WriteLine("MainWindow: SpectrumControl created and added to FftContainer");

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsExpanded))
        {
            AnimateHeight(_viewModel.IsExpanded ? 620 : 220);
        }
    }

    private void AnimateHeight(double targetHeight)
    {
        var animation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        BeginAnimation(HeightProperty, animation);
    }

    private void LibraryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Update SelectedLibraryItem when tree selection changes
        if (sender is TreeView treeView)
        {
            _viewModel.ExpandedPanel.SelectedLibraryItem = treeView.SelectedItem;
        }
    }

    private void LibraryTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem != null)
        {
            _viewModel.ExpandedPanel.DoubleClickLibraryCommand.Execute(null);
        }
    }

    private void Playlist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox && listBox.SelectedItem != null)
        {
            int index = listBox.SelectedIndex;
            if (index >= 0)
            {
                _viewModel.PlayPlaylistItem(index);
            }
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel.ExpandedPanel.SavePlaylist();
        // Settings are auto-saved on each change, but ensure final save
        _viewModel.SaveSettings();
    }

    private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _viewModel.BeginSeek();
    }

    private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _viewModel.EndSeek(_viewModel.Progress);
    }

    private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Handle click on the slider track (not just thumb drag).
        // Calculate fraction from mouse X position relative to slider width.
        if (sender is Slider slider && e.LeftButton == MouseButtonState.Pressed)
        {
            System.Windows.Point pos = e.GetPosition(slider);
            double fraction = pos.X / slider.ActualWidth;
            fraction = Math.Clamp(fraction, 0.0, 1.0);
            _viewModel.EndSeek(fraction);
            e.Handled = true;
        }
    }
}
