using System.Runtime.InteropServices;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr LoadLibrary(string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr R8bCreateDelegate(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqLatency,
        int nChannels,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void R8bDeleteDelegate(IntPtr state);

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

        // Read the first 100 bytes of r8b_create to understand the code
        byte[] code = new byte[100];
        if (ReadProcessMemory(GetCurrentProcess(), createAddr, code, code.Length, out int bytesRead))
        {
            Console.WriteLine("\nFirst 100 bytes of r8b_create:");
            for (int i = 0; i < bytesRead; i += 16)
            {
                Console.Write($"  {i:X4}: ");
                for (int j = 0; j < 16 && i + j < bytesRead; j++)
                    Console.Write($"{code[i + j]:X2} ");
                Console.WriteLine();
            }
        }

        // Let's trace the call instruction at offset 0x3E (call malloc)
        // The call is at offset 0x3E: E8 EA CE 15 00
        // Target = createAddr + 0x3E + 5 + 0x15CEEA = createAddr + 0x15CEF1
        IntPtr mallocAddr = createAddr + 0x3E + 5 + 0x15CEEA;
        Console.WriteLine($"\nMalloc call target: 0x{mallocAddr.ToInt64():X}");

        // Read that function
        byte[] mallocCode = new byte[64];
        if (ReadProcessMemory(GetCurrentProcess(), mallocAddr, mallocCode, mallocCode.Length, out int mallocBytes))
        {
            Console.WriteLine($"\nMalloc function code ({mallocBytes} bytes):");
            for (int i = 0; i < mallocBytes; i += 16)
            {
                Console.Write($"  {i:X4}: ");
                for (int j = 0; j < 16 && i + j < mallocBytes; j++)
                    Console.Write($"{mallocCode[i + j]:X2} ");
                Console.Write("  ");
                for (int j = 0; j < 16 && i + j < mallocBytes; j++)
                    Console.Write(mallocCode[i + j] >= 0x20 && mallocCode[i + j] <= 0x7E ? (char)mallocCode[i + j] : '.');
                Console.WriteLine();
            }
        }

        // Now let's try to call r8b_create with a try/except handler
        Console.WriteLine("\nAttempting r8b_create call...");
        try
        {
            var create = (R8bCreateDelegate)Marshal.GetDelegateForFunctionPointer(createAddr, typeof(R8bCreateDelegate));
            IntPtr state = create(44100.0, 48000.0, 4096, 0.0, 2, 0);
            Console.WriteLine($"  Result: 0x{state.ToInt64():X}");
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
