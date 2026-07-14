using System.ComponentModel;
using System.Runtime.CompilerServices;
using PureAudio.Services;

namespace PureAudio.ViewModels;

/// <summary>
/// Static constants for UI colors used throughout the application.
/// Centralizes color definitions to avoid magic strings.
/// </summary>
public static class UIColors
{
    public const string AccentGold = "#C9A84C";
    public const string DimmedGold = "#A88A3E";
    public const string InactiveGray = "#555555";
    public const string BorderGray = "#4A4A4A";
    public const string PerfectGreen = "#4CAF50";
    public const string LimitedAmber = "#FFC107";
}


/// <summary>
/// Manages all UI indicator properties for the main window.
/// Encapsulates the logic for colors, texts, and visibility of status indicators
/// based on the current audio state (Bit Perfect mode, play state, device capabilities).
/// </summary>
public class StatusDisplayController : INotifyPropertyChanged
{
    private readonly AudioService _audioService;

    // ── State fields ──
    private bool _bitPerfectMode;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _isHiresMode;
    private BitPerfectStatus _bitPerfectStatus = BitPerfectStatus.Off;

    public StatusDisplayController(AudioService audioService)
    {
        _audioService = audioService;
    }

    /// <summary>
    /// Update all internal state fields and refresh all indicator properties.
    /// Call this whenever the audio state changes (track, play state, mode, etc.).
    /// </summary>
    public void UpdateAllIndicators(
        bool bitPerfectMode,
        bool isPlaying,
        bool isPaused,
        bool isHiresMode,
        BitPerfectStatus bitPerfectStatus)
    {
        _bitPerfectMode = bitPerfectMode;
        _isPlaying = isPlaying;
        _isPaused = isPaused;
        _isHiresMode = isHiresMode;
        _bitPerfectStatus = bitPerfectStatus;

        // Notify all indicator properties
        OnPropertyChanged(nameof(BitPerfectButtonColor));
        OnPropertyChanged(nameof(BitPerfectBorderColor));
        OnPropertyChanged(nameof(BitDepthText));
        OnPropertyChanged(nameof(SampleRateText));
        OnPropertyChanged(nameof(BitPerfectInfoText));
        OnPropertyChanged(nameof(IsBitPerfectActive));
        OnPropertyChanged(nameof(BitDepthColor));
        OnPropertyChanged(nameof(SampleRateColor));
        OnPropertyChanged(nameof(BitPerfectIndicatorColor));
        OnPropertyChanged(nameof(PlayIndicatorColor));
        OnPropertyChanged(nameof(PauseIndicatorColor));
        OnPropertyChanged(nameof(StopIndicatorColor));
        OnPropertyChanged(nameof(HiresIndicatorColor));
        OnPropertyChanged(nameof(Mp3IndicatorColor));
        OnPropertyChanged(nameof(IsVolumeActive));
        OnPropertyChanged(nameof(VolumeTextColor));
        OnPropertyChanged(nameof(SourceFormatText));
        OnPropertyChanged(nameof(SourceIndicatorColor));
        OnPropertyChanged(nameof(DacCapabilitiesText));
        OnPropertyChanged(nameof(BitPerfectStatusText));
        OnPropertyChanged(nameof(BitPerfectStatusColor));
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(StatusLabelColor));
        OnPropertyChanged(nameof(DeviceMaxSampleRateText));
        OnPropertyChanged(nameof(DeviceMaxBitDepthText));
        OnPropertyChanged(nameof(DeviceNameText));
    }

    // ════════════════════════════════════════════════════════════════
    //  Bit Perfect Button Indicators
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Color for the Bit Perfect button text — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectButtonColor => _bitPerfectMode ? UIColors.AccentGold : UIColors.InactiveGray;

    /// <summary>
    /// Color for the Bit Perfect button border — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectBorderColor => _bitPerfectMode ? UIColors.AccentGold : UIColors.BorderGray;

    // ════════════════════════════════════════════════════════════════
    //  Format Display (Bit Depth / Sample Rate)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bit depth text (e.g. "24 bit", "16 bit"). Empty when no track is playing.
    /// </summary>
    public string BitDepthText
    {
        get
        {
            int bd = _audioService.CurrentBitDepth;
            return bd > 0 ? $"{bd} bit" : "";
        }
    }

    /// <summary>
    /// Sample rate text (e.g. "96 kHz", "44.1 kHz"). Empty when no track is playing.
    /// Converts raw Hz to kHz with one decimal place for readability.
    /// </summary>
    public string SampleRateText
    {
        get
        {
            int sr = _audioService.CurrentSampleRate;
            if (sr <= 0) return "";
            double khz = sr / 1000.0;
            return khz == (int)khz ? $"{(int)khz} kHz" : $"{khz:F1} kHz";
        }
    }

    /// <summary>
    /// Full info string for tooltip (e.g. "24 bit / 96 kHz").
    /// </summary>
    public string BitPerfectInfoText
    {
        get
        {
            int sr = _audioService.CurrentSampleRate;
            int bd = _audioService.CurrentBitDepth;
            if (sr > 0 && bd > 0)
            {
                double khz = sr / 1000.0;
                string srText = khz == (int)khz ? $"{(int)khz}" : $"{khz:F1}";
                return $"{bd} bit / {srText} kHz";
            }
            return "";
        }
    }

    /// <summary>
    /// Whether bit-perfect indicators should be gold (active).
    /// True when Bit Perfect mode is ON and a track is playing OR paused.
    /// The indicator should NOT turn off during pause — only on Stop.
    /// </summary>
    public bool IsBitPerfectActive => _bitPerfectMode && (_isPlaying || _isPaused) && _audioService.CurrentSampleRate > 0;

    /// <summary>
    /// Color for bit depth text — bright gold when bit-perfect active, gray when inactive.
    /// </summary>
    public string BitDepthColor => IsBitPerfectActive ? UIColors.AccentGold : UIColors.InactiveGray;

    /// <summary>
    /// Color for sample rate text — medium gold when bit-perfect active, gray when inactive.
    /// </summary>
    public string SampleRateColor => IsBitPerfectActive ? UIColors.DimmedGold : UIColors.InactiveGray;

    /// <summary>
    /// Color for the Bit Perfect indicator badge — gold when active, gray when inactive.
    /// </summary>
    public string BitPerfectIndicatorColor => IsBitPerfectActive ? UIColors.AccentGold : UIColors.InactiveGray;

    // ════════════════════════════════════════════════════════════════
    //  Play/Pause/Stop Indicator Colors
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Play indicator color — gold accent when playing.
    /// </summary>
    public string PlayIndicatorColor => _isPlaying ? UIColors.AccentGold : UIColors.InactiveGray;

    /// <summary>
    /// Pause indicator color — gold accent when paused.
    /// </summary>
    public string PauseIndicatorColor => _isPaused ? UIColors.AccentGold : UIColors.InactiveGray;

    /// <summary>
    /// Stop indicator color — gold accent when stopped.
    /// </summary>
    public string StopIndicatorColor => (!_isPlaying && !_isPaused) ? UIColors.AccentGold : UIColors.InactiveGray;

    // ════════════════════════════════════════════════════════════════
    //  Hires/MP3 Indicator Colors
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hires (Lossless) indicator color — gold accent when active.
    /// </summary>
    public string HiresIndicatorColor => _isHiresMode ? UIColors.AccentGold : UIColors.InactiveGray;

    /// <summary>
    /// MP3 (Compressed) indicator color — gold accent when active.
    /// </summary>
    public string Mp3IndicatorColor => !_isHiresMode ? UIColors.AccentGold : UIColors.InactiveGray;

    // ════════════════════════════════════════════════════════════════
    //  Volume Indicators
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Volume slider is only active in Normal (Shared) mode.
    /// In Bit Perfect mode, volume is locked at 100% (no DSP).
    /// </summary>
    public bool IsVolumeActive => !_bitPerfectMode;

    /// <summary>
    /// Volume label color — gold when volume is adjustable (Normal mode),
    /// gray when locked (Bit Perfect mode).
    /// </summary>
    public string VolumeTextColor => _bitPerfectMode ? UIColors.InactiveGray : UIColors.AccentGold;

    // ════════════════════════════════════════════════════════════════
    //  Source / DAC Info
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Source format text for the Source info row, e.g. "24 bit / 96 kHz".
    /// Shows the current track's format. Empty when no track is playing.
    /// </summary>
    public string SourceFormatText
    {
        get
        {
            int sr = _audioService.CurrentSampleRate;
            int bd = _audioService.CurrentBitDepth;
            if (sr <= 0 || bd <= 0) return "";
            double khz = sr / 1000.0;
            string srText = khz == (int)khz ? $"{(int)khz}" : $"{khz:F1}";
            return $"{bd} bit / {srText} kHz";
        }
    }

    /// <summary>
    /// Color for the Source status indicator dot.
    /// Green (#4CAF50) for Perfect, Gold (#FFC107) for Limited, Gray (#555555) for Off.
    /// Stays active during pause — only turns gray on Stop.
    /// </summary>
    public string SourceIndicatorColor
    {
        get
        {
            if (!_bitPerfectMode || (!_isPlaying && !_isPaused))
                return UIColors.InactiveGray;

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => UIColors.PerfectGreen,
                BitPerfectStatus.Limited => UIColors.LimitedAmber,
                _ => UIColors.InactiveGray
            };
        }
    }

    /// <summary>
    /// DAC capabilities text for the DAC info row, e.g. "16-24 bit / 44.1-48 kHz".
    /// </summary>
    public string DacCapabilitiesText
    {
        get
        {
            return _audioService.DeviceCapabilities.DacCapabilitiesText;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Bit Perfect Status Text & Color
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bit Perfect status text (e.g. "Bit Perfect: ✓", "Bit Perfect: Limited", "Bit Perfect: Off").
    /// </summary>
    public string BitPerfectStatusText
    {
        get
        {
            if (!_bitPerfectMode || (!_isPlaying && !_isPaused))
                return "Bit Perfect: Off";

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => "Bit Perfect: ✓",
                BitPerfectStatus.Limited => "Bit Perfect: Limited",
                _ => "Bit Perfect: Off"
            };
        }
    }

    /// <summary>
    /// Color for the Bit Perfect status indicator.
    /// Green for Perfect, yellow for Limited, gray for Off.
    /// Stays active during pause — only turns gray on Stop.
    /// </summary>
    public string BitPerfectStatusColor
    {
        get
        {
            if (!_bitPerfectMode || (!_isPlaying && !_isPaused))
                return UIColors.InactiveGray;

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => UIColors.PerfectGreen,
                BitPerfectStatus.Limited => UIColors.LimitedAmber,
                _ => UIColors.InactiveGray
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Large Status Label (mini-display)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Large status label text shown in the left info area.
    /// "Bit Perfect" when Perfect, "Hi-Q SRC" when Limited, "Standard Mode" when Off.
    /// Stays active during pause — only reverts on Stop.
    /// </summary>
    public string StatusLabelText
    {
        get
        {
            if (!_bitPerfectMode || (!_isPlaying && !_isPaused))
                return "Standard Mode";

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => "Bit Perfect",
                BitPerfectStatus.Limited => "Hi-Q SRC",
                _ => "Standard Mode"
            };
        }
    }

    /// <summary>
    /// Color for the large status label.
    /// Green (#4CAF50) for Perfect, Gold (#FFC107) for Limited, Gray (#555555) for Off.
    /// Stays active during pause — only turns gray on Stop.
    /// </summary>
    public string StatusLabelColor
    {
        get
        {
            if (!_bitPerfectMode || (!_isPlaying && !_isPaused))
                return UIColors.InactiveGray;

            return _bitPerfectStatus switch
            {
                BitPerfectStatus.Perfect => UIColors.PerfectGreen,
                BitPerfectStatus.Limited => UIColors.LimitedAmber,
                _ => UIColors.InactiveGray
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Device Capabilities Display
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maximum sample rate supported by the audio device (e.g. "192 kHz").
    /// </summary>
    public string DeviceMaxSampleRateText
    {
        get
        {
            int maxSr = _audioService.DeviceCapabilities.MaxSampleRate;
            if (maxSr <= 0) return "";
            double khz = maxSr / 1000.0;
            return khz == (int)khz ? $"{(int)khz} kHz" : $"{khz:F1} kHz";
        }
    }

    /// <summary>
    /// Maximum bit depth supported by the audio device (e.g. "32 bit").
    /// </summary>
    public string DeviceMaxBitDepthText
    {
        get
        {
            int maxBd = _audioService.DeviceCapabilities.MaxBitDepth;
            return maxBd > 0 ? $"{maxBd} bit" : "";
        }
    }

    /// <summary>
    /// Audio device name for display.
    /// </summary>
    public string DeviceNameText
    {
        get
        {
            string name = _audioService.DeviceCapabilities.DeviceName;
            return string.IsNullOrEmpty(name) ? "" : name;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  INotifyPropertyChanged
    // ════════════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
