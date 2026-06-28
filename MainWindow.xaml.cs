using PureAudio.Controls;
using PureAudio.Models;
using PureAudio.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PureAudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private SpectrumControl? _spectrumControl;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        // Position window slightly above center to leave room for expanded panel
        Loaded += (s, e) =>
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            Left = (screenWidth - Width) / 2;
            Top = (screenHeight - Height) / 2 - 60; // shift up by 60px
        };

        // Initialize the spectrum control inside the FFT container
        InitializeSpectrumControl();

        // Subscribe to ViewModel property changes for spectrum data
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Handle window closing
        Closing += OnWindowClosing;
    }

    /// <summary>
    /// Creates and initializes the PyQtGraph-style spectrum analyzer
    /// with segmented bars and spring physics animation.
    /// </summary>
    private void InitializeSpectrumControl()
    {
        _spectrumControl = new SpectrumControl
        {
            // The control will be sized by its container
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Insert into the FFT container Grid
        if (FftContainer != null)
        {
            FftContainer.Children.Add(_spectrumControl);
        }
    }

    /// <summary>
    /// Listens for spectrum data updates from the ViewModel.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SpectrumData) && _spectrumControl != null)
        {
            _spectrumControl.Data = _viewModel.SpectrumData;
        }
    }

    // ── Window Control Handlers ──

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Progress Slider Handlers ──

    private void ProgressSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
    }

    private void ProgressSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _viewModel.SeekCommand.Execute(slider.Value);
        }
    }

    private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && e.LeftButton == MouseButtonState.Pressed)
        {
            // Calculate click position as a ratio
            Point clickPoint = e.GetPosition(slider);
            double ratio = clickPoint.X / slider.ActualWidth;
            ratio = Math.Clamp(ratio, 0.0, 1.0);
            _viewModel.SeekCommand.Execute(ratio);
        }
    }

    // ── Window Dragging ──

    /// <summary>
    /// Allows dragging the window by the top bar (since WindowStyle=None).
    /// </summary>
    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    // ── Library Tree Handlers ──

    private void LibraryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LibraryNode node)
        {
            _viewModel.ExpandedPanel.SelectedLibraryItem = node;
        }
    }

    private void LibraryTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem is LibraryNode node)
        {
            _viewModel.ExpandedPanel.DoubleClickLibraryCommand.Execute(node);
        }
    }

    // ── Playlist Handlers ──

    private void Playlist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is PlaylistItem item)
        {
            _viewModel.PlayPlaylistItem(item.Index);
        }
    }

    // ── Cleanup ──

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _spectrumControl?.Dispose();
        _viewModel.SaveSettings();
        _viewModel.ExpandedPanel.SavePlaylist();
    }
}
