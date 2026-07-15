using PureAudio.Controls;
using PureAudio.Models;
using PureAudio.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

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

        // Subscribe to CUE segment changes for the segmented progress bar
        _viewModel.CueSegments.CollectionChanged += OnCueSegmentsChanged;
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

    // ── CUE Segmented Progress Bar ──

    /// <summary>
    /// Called when the CueSegments collection changes.
    /// Re-renders the segment dividers on the canvas.
    /// Uses Dispatcher.BeginInvoke to ensure the canvas has been measured
    /// (ActualWidth is valid) after visibility change from Collapsed to Visible.
    /// </summary>
    private void OnCueSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => RenderCueSegments()), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Renders the CUE segment markers on the CueSegmentsCanvas.
    /// Active segments (tracks present in playlist):
    ///   - Draw a thin dark vertical divider line at the start of each segment
    ///   - The bright slider background shows through between dividers
    /// Inactive segments (tracks removed from playlist):
    ///   - Draw a grey semi-transparent rectangle over the segment area
    ///   - This dims the bright slider background, making the segment look muted
    /// </summary>
    private void RenderCueSegments()
    {
        CueSegmentsCanvas.Children.Clear();

        var segments = _viewModel.CueSegments;
        if (segments.Count == 0)
            return;

        double canvasWidth = CueSegmentsCanvas.ActualWidth;
        if (canvasWidth <= 0)
        {
            // Canvas not yet measured — defer to when it is
            return;
        }

        double canvasHeight = CueSegmentsCanvas.ActualHeight;

        // Resolve theme brushes from application resources
        var activeDividerBrush = TryFindResource("PlaceholderBgBrush") as Brush
                                 ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var inactiveBrush = TryFindResource("InactiveGrayBrush") as Brush
                            ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        // First pass: draw grey dimming rectangles for inactive segments
        foreach (var segment in segments)
        {
            if (segment.IsActive)
                continue;

            double left = segment.StartRatio * canvasWidth;
            double right = segment.EndRatio * canvasWidth;

            left = Math.Max(0, Math.Min(canvasWidth, left));
            right = Math.Max(0, Math.Min(canvasWidth, right));

            double width = right - left;
            if (width <= 0)
                continue;

            var dimRect = new Rectangle
            {
                Width = width,
                Height = canvasHeight,
                Fill = inactiveBrush,
                Opacity = 0.35
            };

            Canvas.SetLeft(dimRect, left);
            CueSegmentsCanvas.Children.Add(dimRect);
        }

        // Second pass: draw dark vertical divider lines at segment boundaries
        foreach (var segment in segments)
        {
            double left = segment.StartRatio * canvasWidth;

            // Skip the very first boundary (left edge of the bar)
            if (left <= 0)
                continue;

            left = Math.Max(0, Math.Min(canvasWidth, left));

            var line = new Rectangle
            {
                Width = 1,
                Height = canvasHeight,
                Fill = activeDividerBrush,
                Opacity = 1.0
            };

            Canvas.SetLeft(line, left);
            CueSegmentsCanvas.Children.Add(line);
        }
    }

    /// <summary>
    /// Re-renders segments when the canvas size changes (e.g., window resize).
    /// </summary>
    private void CueSegmentsCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderCueSegments();
    }

    // ── Cleanup ──


    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _spectrumControl?.Dispose();
        _viewModel.SaveSettings();
        _viewModel.ExpandedPanel.SaveCurrentPlaylist();

    }
}
