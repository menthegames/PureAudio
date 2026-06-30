using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PureAudio.Views;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _progressTimer;
    private double _progress;
    private const double MaxProgress = 0.85; // Leave some room for finalization

    public SplashWindow()
    {
        InitializeComponent();

        // Load the splash image from embedded resources
        try
        {
            var uri = new Uri("pack://application:,,,/PureAudio.png");
            var bitmap = new BitmapImage(uri);
            SplashImage.Source = bitmap;
        }
        catch
        {
            // Ignore if image resource is missing
        }

        // Animate the progress bar smoothly
        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _progressTimer.Tick += OnProgressTick;
        _progressTimer.Start();
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        // Slowly advance progress to show activity
        if (_progress < MaxProgress)
        {
            _progress += 0.008;
            if (_progress > MaxProgress)
                _progress = MaxProgress;

            UpdateProgressBar(_progress);
        }
    }

    /// <summary>
    /// Updates the progress bar to a specific value (0.0 to 1.0).
    /// </summary>
    public void UpdateProgress(double value)
    {
        _progress = Math.Clamp(value, 0.0, 1.0);
        UpdateProgressBar(_progress);
    }

    /// <summary>
    /// Updates the loading status text shown below the progress bar.
    /// </summary>
    public void UpdateStatus(string text)
    {
        LoadingText.Text = text;
    }

    private void UpdateProgressBar(double value)
    {
        // Width of the progress bar track = container width - 40 (20px margin each side)
        double trackWidth = ActualWidth > 0 ? ActualWidth - 40 : 560;
        ProgressBarFill.Width = trackWidth * value;
    }

    /// <summary>
    /// Smoothly fills the progress bar to 100% and closes the splash.
    /// </summary>
    public void CompleteAndClose(Action onCompleted)
    {
        _progressTimer.Stop();

        // Animate to 100% smoothly
        var animation = new DoubleAnimation
        {
            From = _progress,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        animation.Completed += (s, e) =>
        {
            // Small delay so user sees 100%
            var closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            closeTimer.Tick += (s2, e2) =>
            {
                closeTimer.Stop();
                Close();
                onCompleted?.Invoke();
            };
            closeTimer.Start();
        };

        // Animate the progress bar width
        var targetWidth = ActualWidth > 0 ? ActualWidth - 40 : 560;
        ProgressBarFill.BeginAnimation(FrameworkElement.WidthProperty, animation);
        _progress = 1.0;
    }
}
