#r "C:\Users\user\.nuget\packages\naudio\2.3.0\lib\netstandard2.0\NAudio.dll"
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\user\.nuget\packages\naudio\2.3.0\lib\netstandard2.0\NAudio.dll");
foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("Resampler") || t.Name.Contains("SampleRate") || t.Name.Contains("Conversion") || t.Name.Contains("MediaFoundation")))
{
    Console.WriteLine(t.FullName);
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
