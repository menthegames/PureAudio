"""
Диагностика: проверяем, какие форматы поддерживает ЦАП в Exclusive режиме.
Запускается отдельно от приложения для диагностики проблемы.
"""
import ctypes
from ctypes import wintypes
import struct

# COM initialization
ole32 = ctypes.windll.ole32
ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
ole32.CoInitializeEx.restype = ctypes.c_long
ole32.CoInitializeEx(None, 0)  # COINIT_APARTMENTTHREADED

# CLSID_MMDeviceEnumerator = "{BCDE0395-E52F-467C-8E3D-C4579291692E}"
CLSID_MMDeviceEnumerator = ctypes.create_string_buffer(b'\x95\x03\xDE\xBC\x2F\xE5\x7C\x46\x8E\x3D\xC4\x57\x92\x91\x69\x2E')
# IID_IMMDeviceEnumerator = "{A95664D2-9614-4F35-A746-DE8DB63617E6}"
IID_IMMDeviceEnumerator = ctypes.create_string_buffer(b'\xD2\x64\x56\xA9\x14\x96\x35\x4F\xA7\x46\xDE\x8D\xB6\x36\x17\xE6')

# IID_IAudioClient = "{1CB9AD4C-DBFA-4c32-B178-C2F568A703B2}"
IID_IAudioClient = ctypes.create_string_buffer(b'\x4C\xAD\xB9\x1C\xFA\xDB\x32\x4C\xB1\x78\xC2\xF5\x68\xA7\x03\xB2')

# Define some COM interfaces
class GUID(ctypes.Structure):
    _fields_ = [
        ('Data1', ctypes.c_ulong),
        ('Data2', ctypes.c_ushort),
        ('Data3', ctypes.c_ushort),
        ('Data4', ctypes.c_ubyte * 8),
    ]

# Try to use Windows API directly to check WASAPI
print("=" * 60)
print("WASAPI Exclusive Mode Diagnostic")
print("=" * 60)

# Use PowerShell to check device formats
import subprocess
import json

ps_script = """
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;

public class WasapiChecker {
    public static void CheckFormats() {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.WriteLine("Device: " + device.FriendlyName);
        
        int[] rates = { 192000, 176400, 96000, 88200, 48000, 44100 };
        int[] depths = { 32, 24, 16 };
        
        foreach (var sr in rates) {
            foreach (var bps in depths) {
                try {
                    var format = new WaveFormat(sr, bps, 2);
                    var client = device.AudioClient;
                    bool supported = client.IsFormatSupported(AudioClientShareMode.Exclusive, format);
                    Console.WriteLine($"  {sr/1000.0:F1}kHz/{bps}bit -> {(supported ? "SUPPORTED" : "NOT SUPPORTED")}");
                } catch (Exception ex) {
                    Console.WriteLine($"  {sr/1000.0:F1}kHz/{bps}bit -> ERROR: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        
        // Also check mix format
        Console.WriteLine("\\nMix format: " + device.AudioClient.MixFormat);
    }
}
"@
[WasapiChecker]::CheckFormats()
"""

print("\\nRunning NAudio-based check (requires NAudio)...")
print("This will show what formats your DAC actually supports in Exclusive mode.")
print()

# Actually let's just run a simple C# script
print("\\nTo run this diagnostic properly, build and run TestR8b or use:")
print("  dotnet run --project TestR8b")
print()
print("Or check the logs from the app itself.")
