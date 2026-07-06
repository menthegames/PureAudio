using System.Reflection;
// Use the NAudio that's already referenced by the project
var asm = typeof(NAudio.Wave.WaveFormat).Assembly;
Console.WriteLine("=== NAudio Resamplers ===");
foreach (var t in asm.GetExportedTypes().Where(t => 
    t.Name.Contains("Resampler") || 
    t.Name.Contains("SampleRate") || 
    t.Name.Contains("Conversion") || 
    t.Name.Contains("MediaFoundation") ||
    t.Name.Contains("Wdl")))
{
    Console.WriteLine($"\nType: {t.FullName}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({parms})");
    }
    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
    {
        var parms = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  Constructor({parms})");
    }
}
