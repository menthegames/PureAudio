using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using PureAudio.Helpers;
using PureAudio.Models;

namespace PureAudio.Services;

/// <summary>
/// A PCM IWaveProvider that delivers original PCM data for WASAPI Exclusive output.
/// 
/// For WAV/FLAC files, reads original PCM data directly without any conversion.
/// For other formats (MP3, AAC), uses AudioFileReader and converts float back to PCM.
/// 
/// This is the key component for Bit Perfect playback — it delivers raw PCM
/// data directly to the audio driver/DAC in the original format.
/// 
/// Also provides FFT data by converting PCM to float samples for the spectrum analyzer.
/// Supports position tracking and seeking.
/// </summary>
internal class BitPerfectWaveProvider : IWaveProvider, IDisposable
{
    private readonly IWaveProvider _sourceProvider;
    private readonly IDisposable? _disposableSource;
    private readonly FftService? _fftService;
    private readonly FftQueue _fftQueue;
    private readonly AudioFileReader? _audioFileReader; // For non-PCM formats (MP3, AAC, etc.)
    private bool _disposed;

    // For position tracking with PCM sources (WaveFileReader, FlacReader)
    private readonly long _totalPcmBytes;
    private long _pcmPosition;

    // For position tracking with AudioFileReader
    private readonly bool _useAudioFileReader;

    // The original PCM format of the source
    private readonly WaveFormat _pcmFormat;

    // Buffer for float conversion (for FFT)
    private float[]? _floatBuffer;

    // Pre-gain for FFT input in Exclusive mode.
    // Reduces amplitude of PCM→float conversion to prevent spectrum saturation.
    // Adjust this value to control spectrum sensitivity (lower = less sensitive).
    private float _fftPreGain = 0.1f;

    public BitPerfectWaveProvider(string filePath, FftService? fftService = null, FftQueue? fftQueue = null)
    {
        _fftService = fftService;
        _fftQueue = fftQueue ?? new FftQueue(fftService ?? new FftService());
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        switch (ext)
        {
            case ".wav":
            {
                var reader = new WaveFileReader(filePath);
                _sourceProvider = reader;
                _disposableSource = reader;
                _totalPcmBytes = reader.Length;
                _useAudioFileReader = false;

                if (_fftService != null)
                    _fftService.SetSampleRate(reader.WaveFormat.SampleRate);
                break;
            }

            case ".flac":
            {
                var reader = new FlacReader(filePath);
                var format = reader.WaveFormat;
                
                // ВАЛИДАЦИЯ: Проверяем корректность формата
                // Разрешаем любые стандартные битности (8, 16, 24, 32) и любое количество каналов (1-8)
                // Sample rate может быть любым — FlacReader сам знает, что он декодирует
                bool isValidFormat = (format.BitsPerSample == 8 || format.BitsPerSample == 16 || 
                                      format.BitsPerSample == 24 || format.BitsPerSample == 32) &&
                                     (format.Channels >= 1 && format.Channels <= 8);
                
                if (!isValidFormat)
                {
                    Logger.Log($"BitPerfectWaveProvider: FLAC has invalid format {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch, falling back to AudioFileReader");
                    reader.Dispose();
                    
                    // Fallback на AudioFileReader (декодирует в 32-bit Float)
                    var audioReader = new AudioFileReader(filePath);
                    audioReader.Volume = 1.0f;
                    _sourceProvider = audioReader;
                    _disposableSource = audioReader;
                    _audioFileReader = audioReader;
                    _useAudioFileReader = true;
                    _totalPcmBytes = 0; // Неизвестно для AudioFileReader
                }
                else
                {
                    _sourceProvider = reader;
                    _disposableSource = reader;
                    _totalPcmBytes = reader.TotalPcmBytes;
                    _useAudioFileReader = false;
                }
                
                Logger.Log($"BitPerfectWaveProvider: FLAC final source format = {_sourceProvider.WaveFormat.SampleRate}Hz/{_sourceProvider.WaveFormat.BitsPerSample}bit/{_sourceProvider.WaveFormat.Channels}ch");
                if (_fftService != null)
                    _fftService.SetSampleRate(format.SampleRate);
                break;
            }

            default:
            {
                // For MP3, AAC, etc., use AudioFileReader (MediaFoundation).
                // These formats are lossy anyway.
                var reader = new AudioFileReader(filePath);
                reader.Volume = 1.0f;
                _sourceProvider = reader;
                _disposableSource = reader;
                _audioFileReader = reader;
                _useAudioFileReader = true;

                Logger.Log(
                    $"BitPerfectWaveProvider: {ext} - {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.BitsPerSample}bit/{reader.WaveFormat.Channels}ch");

                if (_fftService != null)
                    _fftService.SetSampleRate(reader.WaveFormat.SampleRate);
                break;
            }
        }

        _pcmFormat = _sourceProvider.WaveFormat;

        // ВАЛИДАЦИЯ: Проверяем, что формат реалистичный
        if (_pcmFormat.SampleRate < 8000 || _pcmFormat.SampleRate > 384000 ||
            _pcmFormat.BitsPerSample < 8 || _pcmFormat.BitsPerSample > 32 ||
            _pcmFormat.Channels < 1 || _pcmFormat.Channels > 8)
        {
            Logger.Log($"BitPerfectWaveProvider: INVALID FORMAT detected! " +
                $"SR={_pcmFormat.SampleRate}, BPS={_pcmFormat.BitsPerSample}, CH={_pcmFormat.Channels}");
            throw new InvalidOperationException($"Invalid audio format: {_pcmFormat.SampleRate}Hz/{_pcmFormat.BitsPerSample}bit/{_pcmFormat.Channels}ch");
        }
    }

    public WaveFormat WaveFormat => _pcmFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _sourceProvider.Read(buffer, offset, count);

        if (bytesRead > 0)
        {
            if (!_useAudioFileReader)
            {
                // Track position in PCM bytes
                _pcmPosition += bytesRead;
            }

            // Feed FFT by converting PCM bytes to float samples
            if (_fftService != null)
            {
                FeedFft(buffer, offset, bytesRead);
            }
        }

        return bytesRead;
    }

    /// <summary>
    /// Converts PCM byte data to float samples and feeds them to FFT service.
    /// Applies a pre-gain attenuation to prevent the spectrum from appearing
    /// overly saturated in Exclusive mode (where no Windows mixer volume is applied).
    /// </summary>
    private void FeedFft(byte[] buffer, int offset, int bytesRead)
    {
        try
        {
            int bitsPerSample = _pcmFormat.BitsPerSample;
            int channels = _pcmFormat.Channels;
            
            // ЗАЩИТА: Проверяем корректность битности
            if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
            {
                Logger.Log($"FeedFft: Invalid bitsPerSample={bitsPerSample}, skipping FFT");
                return;
            }
            
            int bytesPerSample = bitsPerSample / 8;
            int totalSamples = bytesRead / bytesPerSample;
            int frames = totalSamples / channels;
            if (frames <= 0) return;
            
            // Allocate or resize float buffer
            if (_floatBuffer == null || _floatBuffer.Length < frames)
                _floatBuffer = new float[frames];
            
            // Convert PCM to float (mono mix)
            for (int i = 0; i < frames; i++)
            {
                float sample = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int byteOffset = offset + (i * channels + ch) * bytesPerSample;
                    if (byteOffset + bytesPerSample > offset + bytesRead)
                        break;
                    float chSample;
                    switch (bitsPerSample)
                    {
                        case 16:
                            short s16 = (short)(buffer[byteOffset] | (buffer[byteOffset + 1] << 8));
                            chSample = s16 / 32768f;
                            break;
                        case 24:
                            int s24 = buffer[byteOffset] |
                                      (buffer[byteOffset + 1] << 8) |
                                      (buffer[byteOffset + 2] << 16);
                            if ((s24 & 0x800000) != 0)
                                s24 |= unchecked((int)0xFF000000);
                            chSample = s24 / 8388608f;
                            break;
                        case 32:
                            int s32 = buffer[byteOffset] |
                                      (buffer[byteOffset + 1] << 8) |
                                      (buffer[byteOffset + 2] << 16) |
                                      (buffer[byteOffset + 3] << 24);
                            chSample = s32 / 2147483648f;
                            break;
                        default:
                            chSample = 0;
                            break;
                    }
                    sample += chSample;
                }
                // Apply pre-gain attenuation and clamp
                _floatBuffer[i] = Math.Clamp(sample / channels * _fftPreGain, -1f, 1f);
            }
            _fftQueue.Enqueue(_floatBuffer);
        }
        catch (Exception ex)
        {
            Logger.Log($"FeedFft: Exception - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Current playback position as TimeSpan.
    /// </summary>
    public TimeSpan CurrentTime
    {
        get
        {
            if (_useAudioFileReader && _audioFileReader != null)
                return _audioFileReader.CurrentTime;

            if (_totalPcmBytes > 0)
            {
                long bytesPerSecond = 0;
                if (_disposableSource is WaveFileReader wfr)
                    bytesPerSecond = wfr.WaveFormat.AverageBytesPerSecond;
                else if (_disposableSource is FlacReader fr)
                    bytesPerSecond = fr.WaveFormat.AverageBytesPerSecond;

                if (bytesPerSecond > 0)
                    return TimeSpan.FromSeconds((double)_pcmPosition / bytesPerSecond);
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Total duration as TimeSpan.
    /// </summary>
    public TimeSpan TotalTime
    {
        get
        {
            if (_useAudioFileReader && _audioFileReader != null)
                return _audioFileReader.TotalTime;

            if (_totalPcmBytes > 0)
            {
                long bytesPerSecond = 0;
                if (_disposableSource is WaveFileReader wfr)
                    bytesPerSecond = wfr.WaveFormat.AverageBytesPerSecond;
                else if (_disposableSource is FlacReader fr)
                    bytesPerSecond = fr.WaveFormat.AverageBytesPerSecond;

                if (bytesPerSecond > 0)
                    return TimeSpan.FromSeconds((double)_totalPcmBytes / bytesPerSecond);
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Seek to a specific position.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (_useAudioFileReader && _audioFileReader != null)
        {
            _audioFileReader.CurrentTime = position;
            return;
        }

        long bytesPerSecond = 0;
        if (_disposableSource is WaveFileReader waveReader)
        {
            bytesPerSecond = waveReader.WaveFormat.AverageBytesPerSecond;
            if (bytesPerSecond > 0)
            {
                long targetByte = (long)(position.TotalSeconds * bytesPerSecond);
                targetByte = Math.Clamp(targetByte, 0, _totalPcmBytes);
                int blockAlign = waveReader.WaveFormat.BlockAlign;
                if (blockAlign > 0)
                    targetByte = (targetByte / blockAlign) * blockAlign;
                waveReader.CurrentTime = position;
                _pcmPosition = waveReader.Position;
            }
        }
        else if (_disposableSource is FlacReader flacReader)
        {
            bytesPerSecond = flacReader.WaveFormat.AverageBytesPerSecond;
            if (bytesPerSecond > 0)
            {
                long targetByte = (long)(position.TotalSeconds * bytesPerSecond);
                targetByte = Math.Clamp(targetByte, 0, _totalPcmBytes);
                int blockAlign = flacReader.WaveFormat.BlockAlign;
                if (blockAlign > 0)
                    targetByte = (targetByte / blockAlign) * blockAlign;
                flacReader.SetPosition(targetByte);
                _pcmPosition = targetByte;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposableSource?.Dispose();
            _disposed = true;
        }
    }
}

public class AudioService : IDisposable
{
    private IWavePlayer? _outputDevice;
    private AudioFileReader? _audioFileReader;
    private BitPerfectWaveProvider? _bitPerfectProvider;
    private SoxResampler? _resampler; // Track the resampler for proper disposal
    private readonly PlaylistService _playlistService;
    private readonly FftService _fftService;
    private readonly FftQueue _fftQueue;
    private readonly DeviceCapabilities _deviceCaps;
    private bool _bitPerfectMode;
    private bool _isPlaying;
    private bool _isPaused;
    private float _volume = 0.5f;
    private float _savedVolume = 0.5f;
    private CancellationTokenSource? _positionCts;
    private TimeSpan _pausePosition;
    private double _pausedProgress;
    private int _playbackId;
    private bool _userStopRequested;
    private BitPerfectStatus _currentBitPerfectStatus = BitPerfectStatus.Off;
    
    // CUE track support
    private CueTrack? _currentCueTrack;
    private bool _isCueTrack;

    public event Action<AudioFile>? TrackChanged;
    public event Action<bool>? PlayStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<int>? BitrateChanged;
    public event Action<float>? VolumeChanged;
    public event Action<bool>? BitPerfectModeChanged;
    /// <summary>
    /// Fired when the Bit Perfect status changes (Off/Perfect/Limited).
    /// The UI uses this to update indicator colors.
    /// </summary>
    public event Action<BitPerfectStatus>? BitPerfectStatusChanged;

    /// <summary>
    /// Provides access to device capabilities for UI display.
    /// </summary>
    public DeviceCapabilities DeviceCapabilities => _deviceCaps;

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public bool BitPerfectMode => _bitPerfectMode;
    public float Volume => _volume;

    /// <summary>
    /// Progress value (0.0 to 1.0) saved at the moment of pause.
    /// Used by UI to keep the progress bar stable during pause in Exclusive mode,
    /// where audio objects are destroyed and position resets to 0.
    /// </summary>
    public double PausedProgress => _pausedProgress;

    /// <summary>
    /// Current Bit Perfect status — indicates whether the track format
    /// matches the device capabilities exactly (Perfect), is limited (Limited),
    /// or Bit Perfect mode is off (Off).
    /// </summary>
    public BitPerfectStatus CurrentBitPerfectStatus => _currentBitPerfectStatus;

    public TimeSpan CurrentPosition
    {
        get
        {
            if (_bitPerfectMode && _resampler != null)
            {
                // When resampler is active, _bitPerfectProvider.CurrentTime already
                // reports the correct position based on source PCM bytes read.
                // No ratio adjustment needed — 1 second of source = 1 second of output.
                // The resampler changes sample rate but not playback duration.
                if (_bitPerfectProvider != null)
                    return _bitPerfectProvider.CurrentTime;
                return TimeSpan.Zero;
            }
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.CurrentTime;
            return _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            // Duration is always the source track duration — resampling doesn't change
            // how long the track is, it just changes the sample rate conversion.
            // The resampler produces outputFrames = inputFrames * ratio, but the
            // playback time is still inputFrames / inputSampleRate = outputFrames / outputSampleRate.
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.TotalTime;
            return _audioFileReader?.TotalTime ?? TimeSpan.Zero;
        }
    }


    public int Bitrate => _playlistService.CurrentItem?.AudioFile.Bitrate ?? 0;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// The UI should show the original track format to the user.
    /// </summary>
    public int CurrentSampleRate
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.WaveFormat.SampleRate;
            return _audioFileReader?.WaveFormat.SampleRate ?? 0;
        }
    }

    /// <summary>
    /// Current bit depth of the playing track (0 if not playing).
    /// ALWAYS returns the SOURCE format (original track format), not the output format.
    /// The UI should show the original track format to the user.
    /// </summary>
    public int CurrentBitDepth
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.WaveFormat.BitsPerSample;
            return _audioFileReader?.WaveFormat.BitsPerSample ?? 0;
        }
    }


    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (!_bitPerfectMode && _audioFileReader != null)
            _audioFileReader.Volume = _volume;
        VolumeChanged?.Invoke(_volume);
    }

    public AudioService(PlaylistService playlistService, FftService fftService)
    {
        _playlistService = playlistService;
        _fftService = fftService;
        _fftQueue = new FftQueue(fftService);
        _deviceCaps = new DeviceCapabilities();
    }

    /// <summary>
    /// Enable or disable Bit Perfect mode.
    /// Bit Perfect mode uses WASAPI Exclusive with raw PCM output.
    /// Normal mode uses WASAPI Shared (system mixer handles volume).
    /// </summary>
    public async void SetBitPerfectMode(bool enable)
    {
        if (_bitPerfectMode == enable)
            return;

        _bitPerfectMode = enable;

        if (_isPlaying || _isPaused)
        {
            var position = CurrentPosition;
            Logger.Log($"SetBitPerfectMode: switching to {(enable ? "Bit Perfect (Exclusive)" : "Normal (Shared)")}, position={position.TotalSeconds:F3}s");

            if (enable)
                _savedVolume = _volume;

            StopInternal();

            if (enable)
                await Task.Delay(150);

            PlayInternal(position);
        }

        BitPerfectModeChanged?.Invoke(_bitPerfectMode);

        // Update Bit Perfect status
        UpdateBitPerfectStatus();
    }

    /// <summary>
    /// Updates the Bit Perfect status based on current mode and track format.
    /// Uses the SOURCE format (before resampling) to determine the true Bit Perfect status.
    /// If resampler is active, the output format will always match the DAC, so we must
    /// check the original source format against the DAC capabilities.
    /// 
    /// Защита от гонок: проверяет согласованность _bitPerfectMode и _isPlaying.
    /// Если режим Bit Perfect выключен, но _isPlaying всё ещё true (или наоборот),
    /// статус принудительно устанавливается в Off.
    /// </summary>
    private void UpdateBitPerfectStatus()
    {
        // Защита от гонок: проверяем согласованность флагов
        bool consistent = _bitPerfectMode == (_outputDevice is WasapiExclusivePlayer);
        if (!consistent)
        {
            Logger.Log($"UpdateBitPerfectStatus: race condition detected — bitPerfectMode={_bitPerfectMode}, outputDevice is WasapiExclusivePlayer={_outputDevice is WasapiExclusivePlayer}. Forcing Off.");
            if (_currentBitPerfectStatus != BitPerfectStatus.Off)
            {
                _currentBitPerfectStatus = BitPerfectStatus.Off;
                BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            }
            return;
        }

        if (!_bitPerfectMode || !_isPlaying)
        {
            if (_currentBitPerfectStatus != BitPerfectStatus.Off)
            {
                _currentBitPerfectStatus = BitPerfectStatus.Off;
                BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            }
            return;
        }

        // IMPORTANT: Always check the SOURCE format (before resampling), not the output format.
        // If resampler is active, the output format already matches the DAC, so checking it
        // would always return Perfect — which is wrong.
        int sr;
        int bd;
        int ch;

        if (_bitPerfectProvider != null)
        {
            // Use the source provider's format (original track format)
            sr = _bitPerfectProvider.WaveFormat.SampleRate;
            bd = _bitPerfectProvider.WaveFormat.BitsPerSample;
            ch = _bitPerfectProvider.WaveFormat.Channels;
        }
        else
        {
            sr = CurrentSampleRate;
            bd = CurrentBitDepth;
            ch = 2;
        }

        var newStatus = _deviceCaps.GetBitPerfectStatus(sr, bd, ch);

        if (_currentBitPerfectStatus != newStatus)
        {
            _currentBitPerfectStatus = newStatus;
            BitPerfectStatusChanged?.Invoke(newStatus);
            Logger.Log($"BitPerfectStatus: {newStatus} (source SR={sr}, BD={bd}, CH={ch})");
        }
    }

    public float GetSavedVolume() => _savedVolume;

    public void Play()
    {
        if (_isPaused)
        {
            Resume();
            return;
        }

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null)
        {
            var firstItem = _playlistService.Items.FirstOrDefault();
            if (firstItem == null) return;
            _playlistService.CurrentIndex = 0;
            currentItem = firstItem;
        }

        PlayInternal(TimeSpan.Zero);
    }

    private void PlayInternal(TimeSpan position)
    {
        Logger.Log($"PlayInternal: requested position = {position.TotalSeconds:F3}s");
        _userStopRequested = false;
        int currentPlaybackId = ++_playbackId;
        StopInternal();

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null) return;

        // Check if this is a CUE track
        _currentCueTrack = currentItem.CueTrack;
        _isCueTrack = _currentCueTrack != null;
        if (_isCueTrack)
        {
            Logger.Log($"PlayInternal: CUE track detected — file={_currentCueTrack!.FilePath}, start={_currentCueTrack.StartPosition}, end={_currentCueTrack.EndPosition}");
        }

        try
        {
            _fftService.Reset();
            _fftQueue.Clear();

            Logger.Log($"PlayInternal: file {currentItem.AudioFile.FilePath}, Metadata says {currentItem.AudioFile.BitsPerSample} bit / {currentItem.AudioFile.SampleRate} Hz");

            if (_bitPerfectMode)
            {
                // === BIT PERFECT PATH ===
                _bitPerfectProvider = new BitPerfectWaveProvider(
                    currentItem.AudioFile.FilePath, _fftService, _fftQueue);
                
                // If this is a CUE track, seek to the start position
                if (_isCueTrack && _currentCueTrack != null)
                {
                    Logger.Log($"PlayInternal (Bit Perfect): seeking to CUE start position {_currentCueTrack.StartPosition}");
                    _bitPerfectProvider.Seek(_currentCueTrack.StartPosition);
                }
                
                int sourceSr = _bitPerfectProvider.WaveFormat.SampleRate;
                int sourceBd = _bitPerfectProvider.WaveFormat.BitsPerSample;
                int sourceCh = _bitPerfectProvider.WaveFormat.Channels;
                
                Logger.Log($"PlayInternal (Bit Perfect): source format={sourceSr}Hz/{sourceBd}bit/{sourceCh}ch");
                
                // Проверяем статус Bit Perfect
                var bpStatus = _deviceCaps.GetBitPerfectStatus(sourceSr, sourceBd, sourceCh);
                Logger.Log($"PlayInternal (Bit Perfect): status={bpStatus}");
                
                IWaveProvider outputProvider = _bitPerfectProvider;
                
                if (bpStatus == BitPerfectStatus.Limited)
                {
                    // Формат не поддерживается напрямую, ищем ближайший поддерживаемый
                    var bestFormat = _deviceCaps.GetBestSupportedFormat(sourceSr, sourceBd, sourceCh);
                    if (bestFormat != null)
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): resampling from {sourceSr}/{sourceBd} to {bestFormat.SampleRate}/{bestFormat.BitsPerSample}");
                        
                        // Используем SoxResampler (NAudio WDL resampler) для качественного ресемплинга
                        try
                        {
                            _resampler = new SoxResampler(_bitPerfectProvider, bestFormat!);
                            outputProvider = _resampler;
                            Logger.Log($"PlayInternal (Bit Perfect): SoxResampler created successfully");
                        }
                        catch (Exception resampleEx)
                        {
                            Logger.Log($"PlayInternal (Bit Perfect): SoxResampler failed: {resampleEx.Message}, falling back to Shared");
                            _resampler = null;
                            bpStatus = BitPerfectStatus.Off;
                        }
                    }
                    else
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): no supported format found, falling back to Shared");
                        bpStatus = BitPerfectStatus.Off;
                    }
                }
                
                if (bpStatus == BitPerfectStatus.Off)
                {
                    // Fallback на Shared режим
                    _bitPerfectProvider.Dispose();
                    _bitPerfectProvider = null;
                    
                    _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                    _audioFileReader.Volume = _volume;
                    if (position < _audioFileReader.TotalTime)
                    {
                        _audioFileReader.CurrentTime = position;
                    }
                    
                    var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(fftProvider);
                    
                    // Обновляем статус на Off
                    _currentBitPerfectStatus = BitPerfectStatus.Off;
                    BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
                }
                else
                {
                    // Exclusive режим с конвертацией или без
                    Logger.Log($"PlayInternal (Bit Perfect): starting Exclusive mode, bpStatus={bpStatus}, outputProvider type={outputProvider.GetType().Name}, format={outputProvider.WaveFormat.SampleRate}Hz/{outputProvider.WaveFormat.BitsPerSample}bit/{outputProvider.WaveFormat.Channels}ch");
                    
                    if (position > TimeSpan.Zero && position < _bitPerfectProvider.TotalTime)
                    {
                        _bitPerfectProvider.Seek(position);
                        
                        // Clear resampler internal buffers after seek to prevent stale data
                        if (_resampler != null)
                        {
                            _resampler.Clear();
                            Logger.Log("PlayInternal (Bit Perfect): cleared resampler after seek");
                        }
                    }
                    
                    _outputDevice = CreateWasapiOutput();
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    
                    try
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): calling _outputDevice.Init()...");
                        _outputDevice.Init(outputProvider);
                        _currentBitPerfectStatus = bpStatus;
                        BitPerfectStatusChanged?.Invoke(bpStatus);
                        Logger.Log($"PlayInternal (Bit Perfect): Init() succeeded, device is in Exclusive mode");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"PlayInternal (Bit Perfect): Init failed: {ex.GetType().Name}: {ex.Message}. Falling back to Shared.");
                        _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                        _outputDevice.Dispose();
                        
                        _bitPerfectProvider?.Dispose();
                        _bitPerfectProvider = null;
                        
                        _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                        _audioFileReader.Volume = _volume;
                        if (position < _audioFileReader.TotalTime)
                        {
                            _audioFileReader.CurrentTime = position;
                        }
                        
                        var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                        _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                        _outputDevice.PlaybackStopped += OnPlaybackStopped;
                        _outputDevice.Init(fftProvider);
                        
                        _currentBitPerfectStatus = BitPerfectStatus.Off;
                        BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
                    }
                }
                
                _outputDevice.Play();
            }
            else
            {
                // === NORMAL (SHARED) PATH ===
                // Используем стандартный AudioFileReader. Он сам конвертирует всё в 32-bit Float, 
                // который идеально подходит для Windows Shared микшера.
                _audioFileReader = new AudioFileReader(currentItem.AudioFile.FilePath);
                _audioFileReader.Volume = _volume;

                Logger.Log($"PlayInternal (Shared): opened file, total={_audioFileReader.TotalTime.TotalSeconds:F3}s, format={_audioFileReader.WaveFormat.SampleRate}Hz/{_audioFileReader.WaveFormat.BitsPerSample}bit/{_audioFileReader.WaveFormat.Channels}ch");

                // If this is a CUE track, seek to the start position
                if (_isCueTrack && _currentCueTrack != null)
                {
                    Logger.Log($"PlayInternal (Shared): seeking to CUE start position {_currentCueTrack.StartPosition}");
                    _audioFileReader.CurrentTime = _currentCueTrack.StartPosition;
                }
                else if (position < _audioFileReader.TotalTime)
                {
                    _audioFileReader.CurrentTime = position;
                }

                // ВАЖНО: AudioFileReader сам реализует ISampleProvider (через WaveStream).
                // Оборачиваем его в FftSampleProvider, чтобы спектр (FFT) работал в Shared режиме.
                // FftSampleProvider принимает ISampleProvider и передаёт данные в FftService.
                var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);

                _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                _outputDevice.PlaybackStopped += OnPlaybackStopped;

                // WasapiOut.Init(ISampleProvider) — принимает ISampleProvider и сам конвертирует в IWaveProvider
                _outputDevice.Init(fftProvider);
                Logger.Log($"PlayInternal (Shared): WasapiOut initialized, calling Play()");
                _outputDevice.Play();
                Logger.Log($"PlayInternal (Shared): Play() called successfully");
            }

            _isPlaying = true;
            _isPaused = false;
            TrackChanged?.Invoke(currentItem.AudioFile);
            DurationChanged?.Invoke(Duration);
            BitrateChanged?.Invoke(Bitrate);
            PlayStateChanged?.Invoke(true);

            // Update Bit Perfect status after track starts
            // Небольшая задержка (80 мс) чтобы дать время на полную инициализацию
            // аудио-устройства перед проверкой статуса Bit Perfect.
            _ = DelayedBitPerfectStatusUpdate();

            StartPositionTracking();
        }
        catch (Exception ex)
        {
            Logger.Log($"PlayInternal: ERROR: {ex.GetType().Name}: {ex.Message}");
            Logger.Log($"PlayInternal: stack trace: {ex.StackTrace}");
            
            // Fallback to Shared mode if anything fails
            try
            {
                StopInternal();
                
                _bitPerfectMode = false;
                BitPerfectModeChanged?.Invoke(false);
                
                var fallbackItem = _playlistService.CurrentItem;
                if (fallbackItem != null)
                {
                    _audioFileReader = new AudioFileReader(fallbackItem.AudioFile.FilePath);
                    _audioFileReader.Volume = _volume;
                    
                    var fftProvider = new FftSampleProvider(_audioFileReader, _fftService, _fftQueue);
                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(fftProvider);
                    _outputDevice.Play();
                    
                    _isPlaying = true;
                    _isPaused = false;
                    TrackChanged?.Invoke(fallbackItem.AudioFile);
                    DurationChanged?.Invoke(Duration);
                    BitrateChanged?.Invoke(Bitrate);
                    PlayStateChanged?.Invoke(true);
                    StartPositionTracking();
                }
            }
            catch (Exception fallbackEx)
            {
                Logger.Log($"PlayInternal: Fallback also failed: {fallbackEx.Message}");
            }
        }
    }

    /// <summary>
    /// Обновляет статус Bit Perfect с небольшой задержкой после старта трека,
    /// чтобы дать время на полную инициализацию аудио-устройства.
    /// </summary>
    private async Task DelayedBitPerfectStatusUpdate()
    {
        try
        {
            await Task.Delay(80);
            UpdateBitPerfectStatus();
        }
        catch (Exception ex)
        {
            Logger.Log($"DelayedBitPerfectStatusUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private IWavePlayer CreateWasapiOutput()
    {
        if (_bitPerfectMode)
        {
            Logger.Log("CreateWasapiOutput: creating WasapiExclusivePlayer (Bit Perfect mode)");
            try
            {
                var exclusivePlayer = new WasapiExclusivePlayer(100);
                Logger.Log("CreateWasapiOutput: WasapiExclusivePlayer created successfully");
                return exclusivePlayer;
            }
            catch (Exception ex)
            {
                Logger.Log($"CreateWasapiOutput: FAILED to create WasapiExclusivePlayer: {ex.GetType().Name}: {ex.Message}");
                Logger.Log("CreateWasapiOutput: falling back to Shared WasapiOut");
                _bitPerfectMode = false;
                BitPerfectModeChanged?.Invoke(false);
                var fallbackWasapi = new WasapiOut(AudioClientShareMode.Shared, 100);
                Logger.Log("CreateWasapiOutput: Shared WasapiOut created as fallback");
                return fallbackWasapi;
            }
        }

        Logger.Log("CreateWasapiOutput: creating Shared WasapiOut");
        return new WasapiOut(AudioClientShareMode.Shared, 100);
    }

    public void Pause()
    {
        if (_outputDevice != null && _isPlaying)
        {
            _pausePosition = CurrentPosition;
            // Сохраняем прогресс для UI, чтобы прогресс-бар не сбросился в 0
            // при уничтожении аудио-объектов в Exclusive режиме
            double duration = Duration.TotalSeconds;
            _pausedProgress = duration > 0 ? _pausePosition.TotalSeconds / duration : 0;
            Logger.Log($"PAUSE: saved position = {_pausePosition.TotalSeconds:F3}s, progress = {_pausedProgress:F4}, bitPerfectMode={_bitPerfectMode}");

            _isPlaying = false;
            _isPaused = true;
            PlayStateChanged?.Invoke(false);

            if (_bitPerfectMode)
            {
                // В Exclusive режиме: используем Pause() из WasapiExclusivePlayer.
                // Он вызывает AudioClient.Stop() без очистки ресурсов,
                // что позволяет быстро возобновить воспроизведение через Play().
                Logger.Log($"PAUSE (Exclusive): using WasapiExclusivePlayer.Pause(), position preserved at {_pausePosition.TotalSeconds:F3}s");
                
                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Pause();
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
            else
            {
                // В Shared режиме: просто стопим, позиция сохраняется в AudioFileReader
                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Stop();
                _outputDevice.PlaybackStopped += OnPlaybackStopped;
            }
        }
    }

    public void Resume()
    {
        if (_isPaused)
        {
            Logger.Log($"RESUME: restoring position = {_pausePosition.TotalSeconds:F3}s, bitPerfectMode={_bitPerfectMode}");

            if (_bitPerfectMode && _outputDevice != null)
            {
                // В Exclusive режиме: просто возобновляем через Play().
                // WasapiExclusivePlayer.Play() вызывает AudioClient.Start(),
                // поток жив, ресурсы не были очищены.
                Logger.Log($"RESUME (Exclusive): calling WasapiExclusivePlayer.Play()");
                _outputDevice.Play();
                _isPlaying = true;
                _isPaused = false;
                PlayStateChanged?.Invoke(true);
            }
            else if (_outputDevice != null && _audioFileReader != null)
            {
                // В Shared режиме: просто продолжаем
                if (_pausePosition < _audioFileReader.TotalTime)
                    _audioFileReader.CurrentTime = _pausePosition;

                _outputDevice.Play();
                _isPlaying = true;
                _isPaused = false;
                PlayStateChanged?.Invoke(true);
            }
            else
            {
                PlayInternal(_pausePosition);
            }
        }
    }

    public void Stop()
    {
        _userStopRequested = true;
        StopInternal();
        _fftService.Reset();
        PlayStateChanged?.Invoke(false);
        UpdateBitPerfectStatus();
    }

    private void StopInternal()
    {
        _positionCts?.Cancel();
        _isPlaying = false;
        _isPaused = false;

        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            _outputDevice.Stop();
            _outputDevice.Dispose();
            _outputDevice = null;
        }

        if (_audioFileReader != null)
        {
            _audioFileReader.Dispose();
            _audioFileReader = null;
        }

        if (_resampler != null)
        {
            _resampler.Dispose();
            _resampler = null;
        }

        if (_bitPerfectProvider != null)
        {
            _bitPerfectProvider.Dispose();
            _bitPerfectProvider = null;
        }
    }

    /// <summary>
    /// Play a specific CUE track by its CueTrack information.
    /// Sets the playlist to the item that contains this CUE track and starts playback
    /// from the CUE track's start position.
    /// </summary>
    public void PlayCueTrack(CueTrack cueTrack)
    {
        Logger.Log($"PlayCueTrack: file={cueTrack.FilePath}, track={cueTrack.TrackNumber}, start={cueTrack.StartPosition}, end={cueTrack.EndPosition}");

        // Find the playlist item that contains this CUE track
        var item = _playlistService.Items.FirstOrDefault(i =>
            i.CueTrack != null &&
            i.CueTrack.FilePath == cueTrack.FilePath &&
            i.CueTrack.TrackNumber == cueTrack.TrackNumber);

        if (item == null)
        {
            Logger.Log("PlayCueTrack: CUE track not found in playlist, searching by file path");
            // Fallback: find any item with the same audio file
            item = _playlistService.Items.FirstOrDefault(i =>
                i.AudioFile.FilePath == cueTrack.FilePath);
        }

        if (item != null)
        {
            int index = _playlistService.Items.IndexOf(item);
            if (index >= 0)
            {
                _playlistService.CurrentIndex = index;
                PlayInternal(TimeSpan.Zero);
            }
        }
        else
        {
            Logger.Log("PlayCueTrack: no matching playlist item found");
        }
    }

    public void Next()
    {
        var next = _playlistService.GetNext();
        if (next != null)
        {
            PlayInternal(TimeSpan.Zero);
        }
        else
        {
            Stop();
        }
    }

    public void Previous()
    {
        var prev = _playlistService.GetPrevious();
        if (prev != null)
        {
            PlayInternal(TimeSpan.Zero);
        }
        else
        {
            Stop();
        }
    }

    public void Seek(double fraction)
    {
        if (_bitPerfectMode && _bitPerfectProvider != null)
        {
            // For CUE tracks, clamp position within the track bounds
            TimeSpan totalTime = _bitPerfectProvider.TotalTime;
            TimeSpan newPosition = TimeSpan.FromTicks((long)(totalTime.Ticks * fraction));
            
            if (_isCueTrack && _currentCueTrack != null)
            {
                // Clamp to CUE track bounds
                TimeSpan cueStart = _currentCueTrack.StartPosition;
                TimeSpan cueEnd = _currentCueTrack.EndPosition;
                TimeSpan cueDuration = cueEnd - cueStart;
                newPosition = cueStart + TimeSpan.FromTicks((long)(cueDuration.Ticks * fraction));
                Logger.Log($"Seek (Bit Perfect, CUE): fraction={fraction:F4}, cueStart={cueStart}, cueEnd={cueEnd}, newPosition={newPosition}");
            }
            
            _bitPerfectProvider.Seek(newPosition);
            
            // If resampler is active, we need to recreate it because the internal
            // accumulation buffer now contains stale data from the old position.
            if (_resampler != null)
            {
                var bestFormat = _deviceCaps.GetBestSupportedFormat(
                    _bitPerfectProvider.WaveFormat.SampleRate,
                    _bitPerfectProvider.WaveFormat.BitsPerSample,
                    _bitPerfectProvider.WaveFormat.Channels);
                
                if (bestFormat != null)
                {
                    _resampler.Dispose();
                    _resampler = new SoxResampler(_bitPerfectProvider, bestFormat);
                    
                    // Re-init the output device with the new resampler
                    if (_outputDevice != null)
                    {
                        _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                        _outputDevice.Stop();
                        _outputDevice.Dispose();
                    }
                    
                    _outputDevice = CreateWasapiOutput();
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(_resampler);
                    _outputDevice.Play();
                }
            }
            
            PositionChanged?.Invoke(newPosition);
        }
        else if (_audioFileReader != null)
        {
            TimeSpan totalTime = _audioFileReader.TotalTime;
            TimeSpan newPosition = TimeSpan.FromTicks((long)(totalTime.Ticks * fraction));
            
            if (_isCueTrack && _currentCueTrack != null)
            {
                // Clamp to CUE track bounds
                TimeSpan cueStart = _currentCueTrack.StartPosition;
                TimeSpan cueEnd = _currentCueTrack.EndPosition;
                TimeSpan cueDuration = cueEnd - cueStart;
                newPosition = cueStart + TimeSpan.FromTicks((long)(cueDuration.Ticks * fraction));
                Logger.Log($"Seek (Shared, CUE): fraction={fraction:F4}, cueStart={cueStart}, cueEnd={cueEnd}, newPosition={newPosition}");
            }
            
            _audioFileReader.CurrentTime = newPosition;
            PositionChanged?.Invoke(newPosition);
        }
    }


    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Logger.Log($"OnPlaybackStopped ENTER: isPlaying={_isPlaying}, isPaused={_isPaused}, sender={sender?.GetType().Name}, outputDevice={_outputDevice?.GetType().Name}, sender==outputDevice={sender == _outputDevice}");

        // Если на паузе — игнорируем (пауза в Exclusive режиме вызывает Stop, что триггерит это событие)
        if (_isPaused)
        {
            Logger.Log("OnPlaybackStopped: ignored (paused)");
            return;
        }

        // Проверяем, что событие пришло от текущего outputDevice
        if (sender != _outputDevice)
        {
            Logger.Log("OnPlaybackStopped: ignored (sender != _outputDevice)");
            return;
        }

        // Естественное окончание трека: _isPlaying может быть уже false (сброшен в StopInternal),
        // но мы всё равно должны перейти к следующему треку.
        // Единственный случай, когда не нужно переходить — это когда пользователь явно нажал Stop.
        // Используем флаг _userStopRequested для этого.
        if (_userStopRequested)
        {
            Logger.Log("OnPlaybackStopped: ignored (user stop requested)");
            return;
        }

        Logger.Log($"OnPlaybackStopped: proceeding to Next() synchronously");

        try
        {
            Next();
        }
        catch (Exception ex)
        {
            Logger.Log($"OnPlaybackStopped: Next() error: {ex.Message}");
        }
    }

    private async void StartPositionTracking()
    {
        _positionCts?.Cancel();
        _positionCts = new CancellationTokenSource();
        var token = _positionCts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token);

                var currentPos = CurrentPosition;
                PositionChanged?.Invoke(currentPos);

                // Check if CUE track has reached its end position
                if (_isCueTrack && _currentCueTrack != null && currentPos >= _currentCueTrack.EndPosition)
                {
                    Logger.Log($"StartPositionTracking: CUE track reached end position {_currentCueTrack.EndPosition}, advancing to next track");
                    _userStopRequested = false;
                    Next();
                    return;
                }
            }
        }
        catch (TaskCanceledException) { }
    }


    public void Dispose()
    {
        StopInternal();
        _deviceCaps.Dispose();
    }
}

