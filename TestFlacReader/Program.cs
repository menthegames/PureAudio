using NAudio.Wave;
using PureAudio.Services;

// Test FlacReader with a specific file
string filePath = args.Length > 0 ? args[0] : @"C:\Users\user\Music\test.flac";

Console.WriteLine($"Testing FlacReader with: {filePath}");
Console.WriteLine($"File exists: {File.Exists(filePath)}");

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found. Please provide a valid FLAC file path.");
    Console.WriteLine("Usage: dotnet run --project TestFlacReader <path-to-flac-file>");
    return;
}

try
{
    var reader = new FlacReader(filePath);
    var format = reader.WaveFormat;
    
    Console.WriteLine($"FlacReader format:");
    Console.WriteLine($"  SampleRate: {format.SampleRate} Hz");
    Console.WriteLine($"  BitsPerSample: {format.BitsPerSample} bit");
    Console.WriteLine($"  Channels: {format.Channels}");
    Console.WriteLine($"  Encoding: {format.Encoding}");
    Console.WriteLine($"  BlockAlign: {format.BlockAlign}");
    Console.WriteLine($"  AverageBytesPerSecond: {format.AverageBytesPerSecond}");
    Console.WriteLine($"  TotalPcmBytes: {reader.TotalPcmBytes}");
    
    // Read some data
    byte[] buffer = new byte[65536];
    int totalRead = 0;
    int bytesRead;
    
    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0 && totalRead < 65536 * 10)
    {
        totalRead += bytesRead;
    }
    
    Console.WriteLine($"Read {totalRead} bytes successfully");
    
    reader.Dispose();
    Console.WriteLine("FlacReader test PASSED!");
}
catch (Exception ex)
{
    Console.WriteLine($"FlacReader FAILED: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
}
