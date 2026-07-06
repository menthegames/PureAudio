using System.Runtime.InteropServices;

class TestR8b
{
    const string DllName = "r8bsrc.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_create")]
    static extern IntPtr r8b_create(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqLatency,
        int nChannels,
        int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_delete")]
    static extern void r8b_delete(IntPtr state);

    static void Main()
    {
        Console.WriteLine("Testing r8bsrc.dll...");
        Console.WriteLine($"Calling r8b_create(44100, 48000, 4096, 0.0, 2, 3)...");
        try
        {
            IntPtr state = r8b_create(44100.0, 48000.0, 4096, 0.0, 2, 3);
            Console.WriteLine($"r8b_create returned: 0x{state.ToInt64():X}");
            if (state != IntPtr.Zero)
            {
                r8b_delete(state);
                Console.WriteLine("r8b_delete called successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine("Done.");
    }
}
