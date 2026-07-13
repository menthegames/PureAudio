using NAudio.CoreAudioApi;
using NAudio.Wave;
using PureAudio.Helpers;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PureAudio.Services;

/// <summary>
/// Кастомный IWavePlayer для WASAPI Exclusive (Bit Perfect) режима.
/// КРИТИЧЕСКИ ВАЖНО: Инициализация AudioClient происходит СИНХРОННО в методе Init(),
/// чтобы AudioService мог перехватить ошибку и сделать Fallback на Shared режим.
/// </summary>
public class WasapiExclusivePlayer : IWavePlayer
{
    private MMDevice? _device;
    private AudioClient? _audioClient;
    private AudioRenderClient? _renderClient;
    private IWaveProvider? _sourceProvider;
    private Thread? _playbackThread;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;
    private volatile bool _stopRequested;
    private WaveFormat? _outputFormat;
    private int _bufferSizeFrames;
    private int _bufferSizeBytes;
    private int _latencyMs = 100;

    /// <summary>
    /// Статическое поле для хранения кода ошибки последней неудачной инициализации WASAPI.
    /// Используется для диагностики: показывает точный код COM-ошибки Windows (например, 0x88890008).
    /// </summary>
    public static string LastInitErrorCode = "";

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public WaveFormat OutputWaveFormat => _outputFormat!;

    public PlaybackState PlaybackState
    {
        get
        {
            if (_isPaused) return NAudio.Wave.PlaybackState.Paused;
            if (_isPlaying) return NAudio.Wave.PlaybackState.Playing;
            return NAudio.Wave.PlaybackState.Stopped;
        }
    }

    public float Volume
    {
        get => 1.0f;
        set { /* Volume is locked at 1.0 in Exclusive mode */ }
    }

    public WasapiExclusivePlayer(int latencyMs = 100)
    {
        _latencyMs = latencyMs;
    }

    // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ 1: Инициализация происходит ЗДЕСЬ, синхронно!
    public void Init(IWaveProvider waveProvider)
    {
        _sourceProvider = waveProvider;
        _outputFormat = waveProvider.WaveFormat;
        Logger.Log($"WasapiExclusivePlayer.Init: format={_outputFormat.SampleRate}Hz/{_outputFormat.BitsPerSample}bit/{_outputFormat.Channels}ch");

        // Вызываем инициализацию синхронно. Если WASAPI отклонит формат или буфер,
        // исключение улетит прямо в AudioService, и сработает Fallback на Shared режим.
        InitializeAudioClient();
        
        if (_audioClient == null || _renderClient == null)
        {
            throw new Exception("WASAPI AudioClient failed to initialize.");
        }
    }

    public void Play()
    {
        if (_sourceProvider == null || _audioClient == null) 
        {
            Logger.Log("WasapiExclusivePlayer: Play() called but source or audioClient is null");
            return;
        }
        
        _stopRequested = false;
        _isPaused = false;
        
        if (_playbackThread != null && _playbackThread.IsAlive)
        {
            _isPlaying = true;
            _audioClient.Start();
            Logger.Log("WasapiExclusivePlayer: Resumed from pause");
            return;
        }
        
        _playbackThread = new Thread(PlaybackThreadProc)
        {
            Name = "WASAPI Exclusive Playback",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        _playbackThread.SetApartmentState(ApartmentState.MTA);
        _playbackThread.Start();
        
        Logger.Log($"WasapiExclusivePlayer: Play() started in Exclusive mode, format={_outputFormat?.SampleRate}Hz/{_outputFormat?.BitsPerSample}bit");
    }

    public void Pause()
    {
        if (!_isPlaying) return;
        
        _isPaused = true;
        _isPlaying = false;
        
        // Останавливаем аудио, но НЕ вызываем Cleanup()!
        // Это сохраняет все ресурсы для возобновления
        _audioClient?.Stop();
        
        Logger.Log("WasapiExclusivePlayer: Paused (resources preserved)");
    }

    public void Stop()
    {
        _stopRequested = true;
        _isPlaying = false;
        _isPaused = false;
        
        _audioClient?.Stop();
        
        // Ждем завершения потока
        if (_playbackThread != null && _playbackThread.IsAlive)
        {
            if (!_playbackThread.Join(1000))
            {
                Logger.Log("WasapiExclusivePlayer: playback thread did not stop in time");
            }
        }
        
        // Теперь очищаем ресурсы
        Cleanup();
    }

    public void Dispose()
    {
        Stop();
    }

    private void PlaybackThreadProc()
    {
        try
        {
            // Инициализация УЖЕ прошла в Init(). Здесь только проверка.
            if (_audioClient == null || _renderClient == null)
            {
                Logger.Log("WasapiExclusivePlayer: AudioClient is null in playback thread");
                RaisePlaybackStopped(false);
                return;
            }

            _isPlaying = true;
            byte[] readBuffer = new byte[_bufferSizeBytes];
            int bytesPerFrame = _outputFormat!.BlockAlign;

            // Pre-fill buffer with silence and start clock
            _renderClient.GetBuffer(_bufferSizeFrames);
            _renderClient.ReleaseBuffer(_bufferSizeFrames, AudioClientBufferFlags.Silent);
            _audioClient.Start();

            while (!_stopRequested)
            {
                if (_isPaused) { Thread.Sleep(10); continue; }

                int paddingFrames = _audioClient.CurrentPadding;
                int availableFrames = _bufferSizeFrames - paddingFrames;
                
                if (availableFrames <= 0)
                {
                    Thread.Sleep(_latencyMs / 4);
                    continue;
                }

                int framesToRead = Math.Min(availableFrames, _bufferSizeFrames / 4);
                int bytesToRead = framesToRead * bytesPerFrame;

                // Чтение данных и кормление FFT (через BitPerfectWaveProvider.Read)
                int bytesRead = _sourceProvider!.Read(readBuffer, 0, bytesToRead);
                if (bytesRead <= 0)
                {
                    Logger.Log("WasapiExclusivePlayer: end of stream reached, breaking out of loop");
                    break;
                }

                int framesRead = bytesRead / bytesPerFrame;
                if (framesRead <= 0) break;

                IntPtr bufferPtr = _renderClient.GetBuffer(framesRead);
                Marshal.Copy(readBuffer, 0, bufferPtr, bytesRead);
                _renderClient.ReleaseBuffer(framesRead, AudioClientBufferFlags.None);
            }

            _audioClient?.Stop();
            RaisePlaybackStopped(false);
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, "WasapiExclusivePlayer: thread exception");
            RaisePlaybackStopped(true);
        }
        finally
        {
            Cleanup();
        }
    }

    // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ 2: Правильная математика WASAPI и очистка COM
    private void InitializeAudioClient()
    {
        // ВАЖНО: Используем FRESH MMDeviceEnumerator для каждого вызова.
        // Не используем _audioClient.IsFormatSupported() перед Initialize() —
        // это может повредить состояние AudioClient и помешать Exclusive режиму.
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _audioClient = _device.AudioClient;

        // ПРАВИЛЬНАЯ МАТЕМАТИКА: 1 мс = 10 000 hns (100-наносекундных интервалов)
        long hnsBufferDuration = _latencyMs * 10000L;

        Logger.Log($"WasapiExclusivePlayer: requesting buffer={hnsBufferDuration} hns ({_latencyMs}ms)");

        // ВАЖНО: НЕ вызываем IsFormatSupported() на этом AudioClient!
        // Проверка формата через IsFormatSupported(Exclusive) может изменить
        // внутреннее состояние AudioClient, что приведёт к сбою Initialize().
        // Проверка формата уже выполнена в DeviceCapabilities до вызова Init().

        try
        {
            _audioClient.Initialize(
                AudioClientShareMode.Exclusive,
                AudioClientStreamFlags.None,
                hnsBufferDuration, // ПЕРЕДАЕМ ВРЕМЯ В hns, А НЕ ФРЕЙМЫ!
                0,
                _outputFormat,
                Guid.Empty);
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            string errorCode = $"0x{comEx.ErrorCode:X8}";
            Logger.Log($"WasapiExclusivePlayer: Initialize FAILED! COMException {errorCode}: {comEx.Message}");
            LastInitErrorCode = errorCode; // Сохраняем код для диагностики
            throw; 
        }
        catch (Exception ex)
        {
            Logger.Log($"WasapiExclusivePlayer: Initialize FAILED! {ex.GetType().Name}: {ex.Message}");
            LastInitErrorCode = ex.GetType().Name;
            throw; 
        }

        _bufferSizeFrames = _audioClient.BufferSize;
        _bufferSizeBytes = _bufferSizeFrames * _outputFormat!.BlockAlign;
        _renderClient = _audioClient.AudioRenderClient;

        Logger.Log($"WasapiExclusivePlayer: AudioClient initialized. Actual buffer = {_bufferSizeFrames} frames");

        // ДИАГНОСТИКА: Проверяем mix format (должен совпадать с нашим форматом в Exclusive режиме)
        Logger.Log($"WasapiExclusivePlayer: AudioClient mix format = {_audioClient.MixFormat?.SampleRate}Hz/{_audioClient.MixFormat?.BitsPerSample}bit/{_audioClient.MixFormat?.Channels}ch");
    }

    private bool _cleanedUp;
    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _isPlaying = false;
        _isPaused = false;
        if (_renderClient != null) { try { _renderClient.Dispose(); } catch { } _renderClient = null; }
        if (_audioClient != null) { try { _audioClient.Dispose(); } catch { } _audioClient = null; }
        if (_device != null) { try { _device.Dispose(); } catch { } _device = null; }
    }

    private void RaisePlaybackStopped(bool fromException)
    {
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(fromException ? new Exception("Playback error") : null));
    }
}
