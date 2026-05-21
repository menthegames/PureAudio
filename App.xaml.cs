using System.Windows;
using PureAudio.Services;
using PureAudio.ViewModels;

namespace PureAudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Create services (lightweight, no heavy I/O)
            var playlistService = new PlaylistService();
            var libraryService = new LibraryService();
            var fftService = new FftService();
            var settingsService = new SettingsService();

            // AudioService starts in Shared mode (safe, no blocking)
            var audioService = new AudioService(playlistService, fftService);

            // Apply non-WASAPI settings immediately
            var settings = settingsService.Current;
            audioService.SetGaplessMode(settings.GaplessEnabled);

            // Create ViewModels
            var expandedPanelVm = new ExpandedPanelViewModel(libraryService, playlistService, audioService);
            var mainViewModel = new MainViewModel(audioService, playlistService, libraryService, fftService, expandedPanelVm, settingsService);

            // Show window IMMEDIATELY — no blocking operations
            var mainWindow = new MainWindow(mainViewModel);
            mainWindow.Show();

            // After window is shown, load everything in background
            mainViewModel.StartBackgroundLoading();
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show($"Startup error: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
