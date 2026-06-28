using System.Windows;
using PureAudio.Services;
using PureAudio.ViewModels;
using PureAudio.Views;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PureAudio;

public partial class App : Application
{
    private SplashWindow? _splash;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private const int MinSplashMs = 4000; // Minimum 4 seconds

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // ── 1. Show splash screen immediately ──
            _splash = new SplashWindow();
            _splash.Show();
            _splash.UpdateStatus("Initializing...");
            DoEvents();

            // ── 2. Create services (lightweight, no heavy I/O) ──
            _splash.UpdateStatus("Loading services...");
            DoEvents();

            var playlistService = new PlaylistService();
            var libraryService = new LibraryService();
            var fftService = new FftService();
            var settingsService = new SettingsService();

            // AudioService starts in Shared mode (safe, no blocking)
            var audioService = new AudioService(playlistService, fftService);

            // Apply non-WASAPI settings immediately
            var settings = settingsService.Current;
            audioService.SetGaplessMode(settings.GaplessEnabled);

            // ── 3. Create ViewModels ──
            _splash.UpdateStatus("Building interface...");
            DoEvents();

            var expandedPanelVm = new ExpandedPanelViewModel(libraryService, playlistService, audioService);
            var mainViewModel = new MainViewModel(audioService, playlistService, libraryService, fftService, expandedPanelVm, settingsService);

            // ── 4. Create main window (but don't show yet) ──
            _splash.UpdateStatus("Preparing player...");
            DoEvents();

            // Load saved named playlists
            playlistService.LoadSavedPlaylistsFromJson();

            var mainWindow = new MainWindow(mainViewModel);

            // ── 5. Ensure minimum splash time (4 seconds) ──
            // Use async startup to keep UI responsive during the minimum delay
            _ = StartupAsync(mainWindow, mainViewModel, playlistService);

        }
        catch (System.Exception ex)
        {
            _splash?.Close();
            System.Windows.MessageBox.Show($"Startup error: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartupAsync(MainWindow mainWindow, MainViewModel mainViewModel, PlaylistService playlistService)

    {
        try
        {
            // Calculate remaining time to meet minimum splash duration
            TimeSpan elapsed = DateTime.UtcNow - _startTime;
            int remainingMs = MinSplashMs - (int)elapsed.TotalMilliseconds;

            if (remainingMs > 0)
            {
                _splash?.UpdateStatus("Loading...");
                DoEvents();

                // Animate progress while waiting asynchronously
                var progressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                progressTimer.Tick += (s, args) =>
                {
                    double elapsedSinceStart = (DateTime.UtcNow - _startTime).TotalMilliseconds;
                    double progress = Math.Min(elapsedSinceStart / MinSplashMs, 0.95);
                    _splash?.UpdateProgress(progress);
                };
                progressTimer.Start();

                await Task.Delay(remainingMs);

                progressTimer.Stop();
            }

            // ── 6. Close splash and show main window ──
            if (_splash != null)
            {
                _splash.UpdateStatus("Ready!");
                _splash.UpdateProgress(1.0);

                _splash.CompleteAndClose(() =>
                {
                    mainWindow.Show();
                    // After window is shown, load everything in background
                    mainViewModel.StartBackgroundLoading();
                });
            }
            else
            {
                // Fallback if splash was closed unexpectedly
                mainWindow.Show();
                mainViewModel.StartBackgroundLoading();
            }
        }
        catch (System.Exception ex)
        {
            _splash?.Close();
            System.Windows.MessageBox.Show($"Startup error: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Forces processing of pending UI events so the splash screen updates visually.
    /// </summary>
    private static void DoEvents()
    {
        Application.Current.Dispatcher.Invoke(
            DispatcherPriority.Background,
            new Action(() => { }));
    }
}
