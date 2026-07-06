using System.Runtime.InteropServices;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr LoadLibrary(string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);

    // Correct delegate signature matching r8bsrc.h:
    // r8b_create(SrcSampleRate, DstSampleRate, MaxInLen, ReqTransBand, Res)
    // Res: 0=16bit, 1=16bitIR, 2=24bit
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr R8bCreateDelegate(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqTransBand,
        int res);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void R8bDeleteDelegate(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int R8bProcessDelegate(
        IntPtr state,
        IntPtr input,
        int inputSampleCount,
        ref IntPtr output);

    static void Main()
    {
        Console.WriteLine("Testing r8bsrc.dll...");

        IntPtr hModule = LoadLibrary("r8bsrc.dll");
        if (hModule == IntPtr.Zero)
        {
            Console.WriteLine($"LoadLibrary failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        Console.WriteLine($"DLL loaded at: 0x{hModule.ToInt64():X}");

        IntPtr createAddr = GetProcAddress(hModule, "r8b_create");
        Console.WriteLine($"r8b_create address: 0x{createAddr.ToInt64():X}");

        IntPtr processAddr = GetProcAddress(hModule, "r8b_process");
        Console.WriteLine($"r8b_process address: 0x{processAddr.ToInt64():X}");

        // Now let's try to call r8b_create with correct signature
        Console.WriteLine("\nAttempting r8b_create call with correct 5-param signature...");
        try
        {
            var create = (R8bCreateDelegate)Marshal.GetDelegateForFunctionPointer(createAddr, typeof(R8bCreateDelegate));
            IntPtr state = create(44100.0, 48000.0, 4096, 2.0, 2);
            Console.WriteLine($"  Result: 0x{state.ToInt64():X}");

            if (state != IntPtr.Zero)
            {
                // Test r8b_process
                var process = (R8bProcessDelegate)Marshal.GetDelegateForFunctionPointer(processAddr, typeof(R8bProcessDelegate));

                double[] inputData = new double[1024];
                for (int i = 0; i < 1024; i++)
                    inputData[i] = Math.Sin(2 * Math.PI * 440 * i / 44100.0);

                GCHandle inputHandle = GCHandle.Alloc(inputData, GCHandleType.Pinned);
                try
                {
                    IntPtr inputPtr = inputHandle.AddrOfPinnedObject();
                    IntPtr outputPtr = IntPtr.Zero;
                    int outputFrames = process(state, inputPtr, 1024, ref outputPtr);
                    Console.WriteLine($"  r8b_process returned {outputFrames} frames, outputPtr=0x{outputPtr.ToInt64():X}");

                    if (outputFrames > 0 && outputPtr != IntPtr.Zero)
                    {
                        double[] outputData = new double[outputFrames * 2];
                        Marshal.Copy(outputPtr, outputData, 0, outputFrames * 2);
                        Console.WriteLine($"  First output sample: {outputData[0]:F6}, {outputData[1]:F6}");
                    }
                }
                finally
                {
                    inputHandle.Free();
                }

                // Cleanup
                var delete = (R8bDeleteDelegate)Marshal.GetDelegateForFunctionPointer(
                    GetProcAddress(hModule, "r8b_delete"), typeof(R8bDeleteDelegate));
                delete(state);
                Console.WriteLine("  r8b_delete called successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Exception: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  StackTrace: {ex.StackTrace}");
        }

        FreeLibrary(hModule);
        Console.WriteLine("\nDone.");
    }
}
