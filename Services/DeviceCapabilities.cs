using NAudio.CoreAudioApi;
using NAudio.Wave;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// Represents the status of Bit Perfect playback for the current track/device combination.
/// </summary>
public enum BitPerfectStatus
{
    /// <summary>Bit Perfect mode is off — playing through Shared mode.</summary>
    Off,
    /// <summary>Bit Perfect mode is active and the track format matches device capabilities exactly.</summary>
    Perfect,
    /// <summary>Bit Perfect mode is active but the device doesn't support the track's native format — resampling or bit depth reduction is happening.</summary>
    Limited
}

/// <summary>
/// Queries WASAPI device capabilities and determines the best format for Bit Perfect playback.
/// 
/// IMPORTANT: This class creates a FRESH MMDeviceEnumerator and AudioClient for EVERY
/// format check operation. This avoids corrupting the AudioClient state which would prevent
/// WasapiExclusivePlayer from initializing in Exclusive mode.
/// 
/// Capabilities are cached after first probe. The cache is invalidated if the default
/// audio device changes (detected by comparing device ID).
/// </summary>
public class DeviceCapabilities
{
    private bool _probed;
    private int _maxSampleRate;
    private int _maxBitDepth;
    private int _maxChannels;
    private string _deviceName = "";
    private string _cachedDeviceId = "";

    /// <summary>Maximum sample rate supported by this device in Exclusive mode.</summary>
    public int MaxSampleRate
    {
        get
        {
            EnsureProbed();
            return _maxSampleRate;
        }
    }

    /// <summary>Maximum bit depth supported by this device in Exclusive mode.</summary>
    public int MaxBitDepth
    {
        get
        {
            EnsureProbed();
            return _maxBitDepth;
        }
    }

    /// <summary>Maximum number of channels supported.</summary>
    public int MaxChannels
    {
        get
        {
            EnsureProbed();
            return _maxChannels;
        }
    }

    /// <summary>Device name.</summary>
    public string DeviceName
    {
        get
        {
            EnsureProbed();
            return _deviceName;
        }
    }

    /// <summary>
    /// Creates a DeviceCapabilities instance.
    /// NOTE: Does NOT probe capabilities in the constructor — probing is lazy.
    /// </summary>
    public DeviceCapabilities()
    {
    }

    /// <summary>
    /// Ensures capabilities have been probed. Called lazily on first access.
    /// If the device has changed since last probe, re-probes automatically.
    /// </summary>
    private void EnsureProbed()
    {
        if (!_probed || HasDeviceChanged())
        {
            ProbeCapabilities();
            _probed = true;
        }
    }

    /// <summary>
    /// Checks if the default audio device has changed since the last probe
    /// by comparing the device ID.
    /// </summary>
    private bool HasDeviceChanged()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string currentId = device.ID ?? "";
            return currentId != _cachedDeviceId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Forces a re-probe of device capabilities. Call this when the audio device
    /// may have changed (e.g., USB DAC disconnected/reconnected).
    /// </summary>
    public void InvalidateCache()
    {
        _probed = false;
        _cachedDeviceId = "";
    }

    /// <summary>
    /// Probes the device for supported formats in Exclusive mode.
    /// Tests common sample rates and bit depths to find the maximum capabilities.
    /// 
    /// Uses a FRESH MMDeviceEnumerator/MMDevice/AudioClient for each format check
    /// to avoid corrupting the AudioClient state for subsequent Exclusive mode initialization.
    /// </summary>
    private void ProbeCapabilities()
    {
        int[] sampleRates = { 192000, 176400, 96000, 88200, 48000, 44100 };
        int[] bitDepths = { 32, 24, 16 };
        int[] channelConfigs = { 2, 6, 8 };

        _maxSampleRate = 0;
        _maxBitDepth = 0;
        _maxChannels = 0;

        // Get device name and ID from a fresh enumerator
        using (var enumForName = new MMDeviceEnumerator())
        using (var devForName = enumForName.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            _deviceName = devForName.FriendlyName;
            _cachedDeviceId = devForName.ID ?? "";
        }

        foreach (var sr in sampleRates)
        {
            foreach (var bps in bitDepths)
            {
                foreach (var ch in channelConfigs)
                {
                    if (IsFormatSupported(sr, bps, ch))
                    {
                        if (sr > _maxSampleRate) _maxSampleRate = sr;
                        if (bps > _maxBitDepth) _maxBitDepth = bps;
                        if (ch > _maxChannels) _maxChannels = ch;
                    }
                }
            }
        }

        Logger.Log(
            $"DeviceCapabilities: {_deviceName} -> " +
            $"MaxSampleRate={_maxSampleRate}Hz, MaxBitDepth={_maxBitDepth}bit, MaxChannels={_maxChannels}ch");
    }

    /// <summary>
    /// Checks if the device supports a specific format in Exclusive mode.
    /// Uses a FRESH MMDeviceEnumerator to create a new MMDevice for each check,
    /// avoiding AudioClient state corruption that would prevent WasapiExclusivePlayer
    /// from initializing.
    /// </summary>
    public bool IsFormatSupported(int sampleRate, int bitsPerSample, int channels)
    {
        try
        {
            var format = new WaveFormat(sampleRate, bitsPerSample, channels);
            // Create a fresh MMDevice to avoid corrupting the main AudioClient state
            using var freshEnumerator = new MMDeviceEnumerator();
            using var freshDevice = freshEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var audioClient = freshDevice.AudioClient;
            return audioClient.IsFormatSupported(AudioClientShareMode.Exclusive, format);
        }
        catch (Exception ex)
        {
            Logger.Log($"IsFormatSupported({sampleRate}/{bitsPerSample}/{channels}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds the best supported format for a given source format.
    /// Tries the exact format first, then falls back to lower bit depths and sample rates.
    /// Returns null if no format is supported (should fall back to Shared mode).
    /// 
    /// IMPORTANT: This searches ALL combinations of sample rates and bit depths,
    /// not just strictly lower ones. For example, if source is 96kHz/24bit and the DAC
    /// supports 48kHz/24bit but not 48kHz/16bit, this will find 48kHz/24bit.
    /// </summary>
    public WaveFormat? GetBestSupportedFormat(int sourceSampleRate, int sourceBitsPerSample, int sourceChannels)
    {
        // Try exact format first
        if (IsFormatSupported(sourceSampleRate, sourceBitsPerSample, sourceChannels))
        {
            return new WaveFormat(sourceSampleRate, sourceBitsPerSample, sourceChannels);
        }

        // Try same sample rate with any bit depth (lower or equal)
        int[] bitDepths = { 32, 24, 16 };
        foreach (var bps in bitDepths)
        {
            if (bps != sourceBitsPerSample && IsFormatSupported(sourceSampleRate, bps, sourceChannels))
            {
                return new WaveFormat(sourceSampleRate, bps, sourceChannels);
            }
        }

        // Try all sample rates (preferring closest to source) with all bit depths
        int[] sampleRates = { 192000, 176400, 96000, 88200, 48000, 44100 };
        
        // First pass: try same bit depth with lower sample rates
        foreach (var sr in sampleRates)
        {
            if (sr != sourceSampleRate && IsFormatSupported(sr, sourceBitsPerSample, sourceChannels))
            {
                return new WaveFormat(sr, sourceBitsPerSample, sourceChannels);
            }
        }

        // Second pass: try all combinations of sample rates and bit depths
        foreach (var sr in sampleRates)
        {
            if (sr == sourceSampleRate) continue; // already tried above
            foreach (var bps in bitDepths)
            {
                if (bps == sourceBitsPerSample) continue; // already tried in first pass
                if (IsFormatSupported(sr, bps, sourceChannels))
                {
                    return new WaveFormat(sr, bps, sourceChannels);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Determines the Bit Perfect status for a given source format.
    /// </summary>
    public BitPerfectStatus GetBitPerfectStatus(int sourceSampleRate, int sourceBitsPerSample, int sourceChannels)
    {
        if (sourceSampleRate <= 0 || sourceBitsPerSample <= 0)
            return BitPerfectStatus.Off;

        if (IsFormatSupported(sourceSampleRate, sourceBitsPerSample, sourceChannels))
            return BitPerfectStatus.Perfect;

        // Check if any format is supported at all in Exclusive mode
        var best = GetBestSupportedFormat(sourceSampleRate, sourceBitsPerSample, sourceChannels);
        if (best != null)
            return BitPerfectStatus.Limited;

        return BitPerfectStatus.Off;
    }

    /// <summary>
    /// Gets the mix format of the device (what Shared mode uses).
    /// Uses a FRESH MMDevice to avoid state corruption.
    /// </summary>
    public WaveFormat MixFormat
    {
        get
        {
            using var freshEnumerator = new MMDeviceEnumerator();
            using var freshDevice = freshEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var audioClient = freshDevice.AudioClient;
            return audioClient.MixFormat;
        }
    }

    /// <summary>
    /// Returns a compact string describing the DAC's capabilities range,
    /// e.g. "16-24 bit / 44.1-48 kHz".
    /// </summary>
    public string DacCapabilitiesText
    {
        get
        {
            EnsureProbed();
            if (_maxBitDepth <= 0 || _maxSampleRate <= 0)
                return "";

            // Build bit depth range string
            string bitDepthStr;
            int[] bitDepths = { 32, 24, 16 };
            int minBd = 32, maxBd = 0;
            foreach (var bps in bitDepths)
            {
                if (IsFormatSupported(44100, bps, 2))
                {
                    if (bps < minBd) minBd = bps;
                    if (bps > maxBd) maxBd = bps;
                }
            }
            if (maxBd == 0)
                bitDepthStr = $"{_maxBitDepth} bit";
            else if (minBd == maxBd)
                bitDepthStr = $"{minBd} bit";
            else
                bitDepthStr = $"{minBd}-{maxBd} bit";

            // Build sample rate range string
            int[] sampleRates = { 192000, 176400, 96000, 88200, 48000, 44100 };
            int minSr = 192000, maxSr = 0;
            foreach (var sr in sampleRates)
            {
                if (IsFormatSupported(sr, 16, 2))
                {
                    if (sr < minSr) minSr = sr;
                    if (sr > maxSr) maxSr = sr;
                }
            }
            string srStr;
            if (maxSr == 0)
                srStr = $"{_maxSampleRate / 1000.0:0.#} kHz";
            else if (minSr == maxSr)
                srStr = $"{minSr / 1000.0:0.#} kHz";
            else
                srStr = $"{minSr / 1000.0:0.#}-{maxSr / 1000.0:0.#} kHz";

            return $"{bitDepthStr} / {srStr}";
        }
    }

    /// <summary>
    /// Возвращает строку со списком всех поддерживаемых ЦАПом форматов (для диагностики).
    /// </summary>
    public string GetSupportedFormatsReport()
    {
        var supported = new System.Collections.Generic.List<string>();
        int[] sampleRates = { 192000, 176400, 96000, 88200, 48000, 44100 };
        int[] bitDepths = { 32, 24, 16 };
        
        foreach (var sr in sampleRates)
        {
            foreach (var bps in bitDepths)
            {
                // Проверяем только стерео (2 канала), так как музыка обычно в стерео
                if (IsFormatSupported(sr, bps, 2)) 
                {
                    double khz = sr / 1000.0;
                    string srText = khz == (int)khz ? $"{(int)khz}" : $"{khz:F1}";
                    supported.Add($"{srText} kHz / {bps} bit");
                }
            }
        }
        
        if (supported.Count == 0) 
            return "ЦАП не поддерживает Exclusive режим или устройство занято другим приложением.";
        
        return string.Join("\n", supported);
    }

    public void Dispose()
    {
        // No resources to dispose — all MMDevice instances are created
        // with 'using' and disposed immediately.
    }
}
