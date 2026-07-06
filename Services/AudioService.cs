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

    public BitPerfectWaveProvider(string filePath, FftService? fftService = null)
    {
        _fftService = fftService;
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

                Logger.Log(
                    $"BitPerfectWaveProvider: WAV - {reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.BitsPerSample}bit/{reader.WaveFormat.Channels}ch");

                if (_fftService != null)
                    _fftService.SetSampleRate(reader.WaveFormat.SampleRate);
                break;
            }

            case ".flac":
            {
                var reader = new FlacReader(filePath);
                var format = reader.WaveFormat;
                
                Logger.Log($"BitPerfectWaveProvider: FlacReader format = {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch, Encoding={format.Encoding}, BlockAlign={format.BlockAlign}, AverageBytesPerSecond={format.AverageBytesPerSecond}");
                
                // ВАЛИДАЦИЯ: Проверяем корректность формата
                bool isValidFormat = (format.BitsPerSample == 8 || format.BitsPerSample == 16 || 
                                      format.BitsPerSample == 24 || format.BitsPerSample == 32) &&
                                     (format.SampleRate == 44100 || format.SampleRate == 48000 || 
                                      format.SampleRate == 88200 || format.SampleRate == 96000 || 
                                      format.SampleRate == 176400 || format.SampleRate == 192000 || 
                                      format.SampleRate == 352800 || format.SampleRate == 384000) &&
                                     (format.Channels == 1 || format.Channels == 2);
                
                Logger.Log($"BitPerfectWaveProvider: FLAC isValidFormat={isValidFormat}");
                
                if (!isValidFormat)
                {
                    Logger.Log($"BitPerfectWaveProvider: FLAC has invalid format {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch, falling back to AudioFileReader");
                    reader.Dispose();
                    
                    // Fallback на AudioFileReader (декодирует в 32-bit Float)
                    var audioReader = new AudioFileReader(filePath);
                    audioReader.Volume = 1.0f;
                    Logger.Log($"BitPerfectWaveProvider: AudioFileReader format = {audioReader.WaveFormat.SampleRate}Hz/{audioReader.WaveFormat.BitsPerSample}bit/{audioReader.WaveFormat.Channels}ch, Encoding={audioReader.WaveFormat.Encoding}");
                    _sourceProvider = audioReader;
                    _disposableSource = audioReader;
                    _audioFileReader = audioReader;
                    _useAudioFileReader = true;
                    _totalPcmBytes = 0; // Неизвестно для AudioFileReader
                }
                else
                {
                    Logger.Log($"BitPerfectWaveProvider: using FlacReader directly");
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
                _floatBuffer[i] = Math.Clamp(sample / channels, -1f, 1f);
            }
            _fftService!.ProcessSamples(_floatBuffer);
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
    private readonly PlaylistService _playlistService;
    private readonly FftService _fftService;
    private readonly DeviceCapabilities _deviceCaps;
    private bool _bitPerfectMode;
    private bool _isPlaying;
    private bool _isPaused;
    private float _volume = 0.5f;
    private float _savedVolume = 0.5f;
    private CancellationTokenSource? _positionCts;
    private TimeSpan _pausePosition;
    private int _playbackId;
    private BitPerfectStatus _currentBitPerfectStatus = BitPerfectStatus.Off;

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
    /// Current Bit Perfect status — indicates whether the track format
    /// matches the device capabilities exactly (Perfect), is limited (Limited),
    /// or Bit Perfect mode is off (Off).
    /// </summary>
    public BitPerfectStatus CurrentBitPerfectStatus => _currentBitPerfectStatus;

    public TimeSpan CurrentPosition
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.CurrentTime;
            return _audioFileReader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_bitPerfectMode && _bitPerfectProvider != null)
                return _bitPerfectProvider.TotalTime;
            return _audioFileReader?.TotalTime ?? TimeSpan.Zero;
        }
    }

    public int Bitrate => _playlistService.CurrentItem?.AudioFile.Bitrate ?? 0;

    /// <summary>
    /// Current sample rate of the playing track (0 if not playing).
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
    /// </summary>
    private void UpdateBitPerfectStatus()
    {
        if (!_bitPerfectMode || !_isPlaying)
        {
            if (_currentBitPerfectStatus != BitPerfectStatus.Off)
            {
                _currentBitPerfectStatus = BitPerfectStatus.Off;
                BitPerfectStatusChanged?.Invoke(BitPerfectStatus.Off);
            }
            return;
        }

        int sr = CurrentSampleRate;
        int bd = CurrentBitDepth;
        int ch = _bitPerfectProvider?.WaveFormat.Channels ?? 2;

        var newStatus = _deviceCaps.GetBitPerfectStatus(sr, bd, ch);

        if (_currentBitPerfectStatus != newStatus)
        {
            _currentBitPerfectStatus = newStatus;
            BitPerfectStatusChanged?.Invoke(newStatus);
            Logger.Log($"BitPerfectStatus: {newStatus} (SR={sr}, BD={bd}, CH={ch})");
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
        int currentPlaybackId = ++_playbackId;
        StopInternal();

        var currentItem = _playlistService.CurrentItem;
        if (currentItem == null) return;

        try
        {
            _fftService.Reset();

            Logger.Log($"PlayInternal: file {currentItem.AudioFile.FilePath}, Metadata says {currentItem.AudioFile.BitsPerSample} bit / {currentItem.AudioFile.SampleRate} Hz");

            if (_bitPerfectMode)
            {
                // === BIT PERFECT PATH ===
                _bitPerfectProvider = new BitPerfectWaveProvider(
                    currentItem.AudioFile.FilePath, _fftService);
                
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
                        
                        // Используем R8brainResampler для качественного ресемплинга
                        try
                        {
                            if (R8brainResampler.IsDllAvailable())
                            {
                                outputProvider = new R8brainResampler(_bitPerfectProvider, bestFormat!);
                            }
                            else
                            {
                                Logger.Log($"PlayInternal (Bit Perfect): r8bsrc.dll not available, falling back to Shared");
                                bpStatus = BitPerfectStatus.Off;
                            }
                        }
                        catch (Exception resampleEx)
                        {
                            Logger.Log($"PlayInternal (Bit Perfect): R8brainResampler failed: {resampleEx.Message}, falling back to Shared");
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
                    
                    var fftProvider = new FftSampleProvider(_audioFileReader, _fftService);
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
                    if (position > TimeSpan.Zero && position < _bitPerfectProvider.TotalTime)
                    {
                        _bitPerfectProvider.Seek(position);
                    }
                    
                    _outputDevice = CreateWasapiOutput();
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    
                    try
                    {
                        _outputDevice.Init(outputProvider);
                        _currentBitPerfectStatus = bpStatus;
                        BitPerfectStatusChanged?.Invoke(bpStatus);
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
                        
                        var fftProvider = new FftSampleProvider(_audioFileReader, _fftService);
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

                if (position < _audioFileReader.TotalTime)
                {
                    _audioFileReader.CurrentTime = position;
                }

                // ВАЖНО: AudioFileReader сам реализует ISampleProvider (через WaveStream).
                // Оборачиваем его в FftSampleProvider, чтобы спектр (FFT) работал в Shared режиме.
                // FftSampleProvider принимает ISampleProvider и передаёт данные в FftService.
                var fftProvider = new FftSampleProvider(_audioFileReader, _fftService);

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
            UpdateBitPerfectStatus();

            StartPositionTracking();
        }
        catch (Exception ex)
        {
            Logger.Log($"Playback error (playbackId={currentPlaybackId}): {ex.GetType().Name}: {ex.Message}");
            Logger.Log($"Stack: {ex.StackTrace}");
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
                Logger.Log($"CreateWasapiOutput: Stack: {ex.StackTrace}");
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
            Logger.Log($"PAUSE: saved position = {_pausePosition.TotalSeconds:F3}s, bitPerfectMode={_bitPerfectMode}");

            _isPlaying = false;
            _isPaused = true;
            PlayStateChanged?.Invoke(false);

            if (_bitPerfectMode)
            {
                // В Exclusive режиме: полная остановка с очисткой ресурсов.
                // WASAPI Exclusive не поддерживает паузу с сохранением позиции —
                // AudioClient.Stop() сбрасывает состояние буфера.
                // При возобновлении сделаем полный перезапуск через PlayInternal(_pausePosition).
                Logger.Log($"PAUSE (Exclusive): doing full stop+cleanup, will resume from {_pausePosition.TotalSeconds:F3}s");
                
                // Отписываемся, чтобы OnPlaybackStopped не вызвал Next()
                _outputDevice.PlaybackStopped -= OnPlaybackStopped;
                _outputDevice.Stop();
                _outputDevice.Dispose();
                _outputDevice = null;
                
                if (_bitPerfectProvider != null)
                {
                    _bitPerfectProvider.Dispose();
                    _bitPerfectProvider = null;
                }
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

            if (_bitPerfectMode)
            {
                // В Exclusive режиме: полный перезапуск с сохранённой позиции
                Logger.Log($"RESUME (Exclusive): full restart from {_pausePosition.TotalSeconds:F3}s");
                PlayInternal(_pausePosition);
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

        if (_bitPerfectProvider != null)
        {
            _bitPerfectProvider.Dispose();
            _bitPerfectProvider = null;
        }
    }

    public void Next()
    {
        var next = _playlistService.GetNext();
        if (next != null)
            PlayInternal(TimeSpan.Zero);
        else
            Stop();
    }

    public void Previous()
    {
        var prev = _playlistService.GetPrevious();
        if (prev != null)
            PlayInternal(TimeSpan.Zero);
        else
            Stop();
    }

    public void Seek(double fraction)
    {
        if (_bitPerfectMode && _bitPerfectProvider != null)
        {
            var newPosition = TimeSpan.FromTicks((long)(_bitPerfectProvider.TotalTime.Ticks * fraction));
            _bitPerfectProvider.Seek(newPosition);
            PositionChanged?.Invoke(newPosition);
        }
        else if (_audioFileReader != null)
        {
            var newPosition = TimeSpan.FromTicks((long)(_audioFileReader.TotalTime.Ticks * fraction));
            _audioFileReader.CurrentTime = newPosition;
            PositionChanged?.Invoke(newPosition);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!_isPlaying || _isPaused)
            return;

        if (sender != _outputDevice)
            return;

        Logger.Log($"OnPlaybackStopped: isPlaying={_isPlaying}, isPaused={_isPaused}");

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Next();
            }
            catch (Exception ex)
            {
                Logger.Log($"Async Next() error: {ex.Message}");
            }
        });
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

                if (_bitPerfectMode && _bitPerfectProvider != null)
                    PositionChanged?.Invoke(_bitPerfectProvider.CurrentTime);
                else if (_audioFileReader != null)
                    PositionChanged?.Invoke(_audioFileReader.CurrentTime);
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

