using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DiagnoseExclusive;

/// <summary>
/// Диагностика: проверяем, какие форматы поддерживает ЦАП в Exclusive режиме.
/// Запускать: dotnet run --project DiagnoseExclusive
/// </summary>
class Program
{
    static void Main()
    {
        Console.WriteLine("=".PadRight(70, '='));
        Console.WriteLine("WASAPI EXCLUSIVE MODE DIAGNOSTIC");
        Console.WriteLine("=".PadRight(70, '='));

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        
        Console.WriteLine($"Device: {device.FriendlyName}");
        Console.WriteLine($"Device ID: {device.ID}");
        Console.WriteLine($"State: {device.State}");
        Console.WriteLine();

        // Check mix format
        using var mixClient = device.AudioClient;
        var mixFormat = mixClient.MixFormat;
        Console.WriteLine($"Mix format (Shared mode): {mixFormat.SampleRate}Hz/{mixFormat.BitsPerSample}bit/{mixFormat.Channels}ch");
        Console.WriteLine();

        // Test Exclusive formats
        int[] rates = { 384000, 352800, 192000, 176400, 96000, 88200, 48000, 44100 };
        int[] depths = { 32, 24, 16 };
        int[] channels = { 2, 6, 8 };

        Console.WriteLine("Testing formats in Exclusive mode:");
        Console.WriteLine("-".PadRight(70, '-'));
        
        foreach (var sr in rates)
        {
            foreach (var bps in depths)
            {
                foreach (var ch in channels)
                {
                    TestFormat(sr, bps, ch);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=".PadRight(70, '='));
        Console.WriteLine("Testing with FRESH AudioClient each time (like DeviceCapabilities does):");
        Console.WriteLine("=".PadRight(70, '='));

        foreach (var sr in rates)
        {
            foreach (var bps in depths)
            {
                TestFormatFresh(sr, bps, 2);
            }
        }

        Console.WriteLine();
        Console.WriteLine("=".PadRight(70, '='));
        Console.WriteLine("Now testing actual Exclusive initialization (like WasapiExclusivePlayer does):");
        Console.WriteLine("=".PadRight(70, '='));

        // Try to actually initialize Exclusive mode with the mix format
        TestExclusiveInit(mixFormat);
        
        // Try common formats
        TestExclusiveInit(new WaveFormat(44100, 16, 2));
        TestExclusiveInit(new WaveFormat(48000, 16, 2));
        TestExclusiveInit(new WaveFormat(96000, 24, 2));
        TestExclusiveInit(new WaveFormat(192000, 24, 2));

        Console.WriteLine();
        Console.WriteLine("Diagnostic complete.");
    }

    static void TestFormat(int sampleRate, int bitsPerSample, int channels)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = device.AudioClient;
            
            var format = new WaveFormat(sampleRate, bitsPerSample, channels);
            bool supported = client.IsFormatSupported(AudioClientShareMode.Exclusive, format);
            
            string srText = sampleRate >= 1000 ? $"{sampleRate / 1000.0:F1}kHz" : $"{sampleRate}Hz";
            Console.WriteLine($"  {srText,7}/{bitsPerSample}bit/{channels}ch -> {(supported ? "YES" : "NO")}");
        }
        catch (Exception ex)
        {
            string srText = sampleRate >= 1000 ? $"{sampleRate / 1000.0:F1}kHz" : $"{sampleRate}Hz";
            Console.WriteLine($"  {srText,7}/{bitsPerSample}bit/{channels}ch -> ERROR: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void TestFormatFresh(int sampleRate, int bitsPerSample, int channels)
    {
        try
        {
            // FRESH enumerator each time (like DeviceCapabilities does)
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = device.AudioClient;
            
            var format = new WaveFormat(sampleRate, bitsPerSample, channels);
            bool supported = client.IsFormatSupported(AudioClientShareMode.Exclusive, format);
            
            string srText = sampleRate >= 1000 ? $"{sampleRate / 1000.0:F1}kHz" : $"{sampleRate}Hz";
            Console.WriteLine($"  {srText,7}/{bitsPerSample}bit/{channels}ch -> {(supported ? "YES" : "NO")}");
        }
        catch (Exception ex)
        {
            string srText = sampleRate >= 1000 ? $"{sampleRate / 1000.0:F1}kHz" : $"{sampleRate}Hz";
            Console.WriteLine($"  {srText,7}/{bitsPerSample}bit/{channels}ch -> ERROR: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void TestExclusiveInit(WaveFormat format)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = device.AudioClient;
            
            Console.WriteLine($"\\nTrying Exclusive init with {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch...");
            
            client.Initialize(
                AudioClientShareMode.Exclusive,
                AudioClientStreamFlags.None,
                100 * 10000L, // 100ms in hns
                0,
                format,
                Guid.Empty);
            
            Console.WriteLine($"  SUCCESS! Buffer size: {client.BufferSize} frames");
            Console.WriteLine($"  Mix format after init: {client.MixFormat?.SampleRate}Hz/{client.MixFormat?.BitsPerSample}bit");
            
            // Try to get render client
            using var renderClient = client.AudioRenderClient;
            Console.WriteLine($"  RenderClient obtained: {renderClient != null}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex is System.Runtime.InteropServices.COMException comEx)
            {
                Console.WriteLine($"  COM Error Code: 0x{comEx.ErrorCode:X8}");
            }
        }
    }
}
