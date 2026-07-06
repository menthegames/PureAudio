using System.Reflection;

// Load the SoxSharp assembly directly
var asm = Assembly.LoadFrom(@"C:\Users\user\.nuget\packages\soxsharp\1.3.5\lib\netstandard2.0\SoxSharp.dll");

Console.WriteLine("=== SoxSharp API Inspection ===");
Console.WriteLine($"Assembly: {asm.FullName}");

foreach (var t in asm.GetExportedTypes())
{
    Console.WriteLine($"\nType: {t.FullName}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({parms})");
    }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"  Property: {p.PropertyType.Name} {p.Name}");
    }
    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
    {
        var parms = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  Constructor({parms})");
    }
}
