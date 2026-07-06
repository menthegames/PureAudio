using System.Runtime.InteropServices;

class TestR8b
{
    const string DllName = "r8bsrc.dll";

    // Correct P/Invoke signature matching r8bsrc.h:
    // r8b_create(SrcSampleRate, DstSampleRate, MaxInLen, ReqTransBand, Res)
    // Res: 0=16bit, 1=16bitIR, 2=24bit
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_create")]
    static extern IntPtr r8b_create(
        double srcSampleRate,
        double dstSampleRate,
        int maxInLen,
        double reqTransBand,
        int res);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_delete")]
    static extern void r8b_delete(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "r8b_process")]
    static extern int r8b_process(
        IntPtr state,
        IntPtr input,
        int inputSampleCount,
        ref IntPtr output);

    static void Main()
    {
        Console.WriteLine("Testing r8bsrc.dll...");
        Console.WriteLine($"Calling r8b_create(44100, 48000, 4096, 2.0, 2)...");
        try
        {
            IntPtr state = r8b_create(44100.0, 48000.0, 4096, 2.0, 2);
            Console.WriteLine($"r8b_create returned: 0x{state.ToInt64():X}");
            if (state != IntPtr.Zero)
            {
                // Test r8b_process with some dummy data
                double[] inputData = new double[1024]; // 1024 samples per channel
                for (int i = 0; i < 1024; i++)
                    inputData[i] = Math.Sin(2 * Math.PI * 440 * i / 44100.0); // 440Hz sine at 44100

                GCHandle inputHandle = GCHandle.Alloc(inputData, GCHandleType.Pinned);
                try
                {
                    IntPtr inputPtr = inputHandle.AddrOfPinnedObject();
                    IntPtr outputPtr = IntPtr.Zero;
                    int outputFrames = r8b_process(state, inputPtr, 1024, ref outputPtr);
                    Console.WriteLine($"r8b_process returned {outputFrames} output frames, outputPtr=0x{outputPtr.ToInt64():X}");

                    if (outputFrames > 0 && outputPtr != IntPtr.Zero)
                    {
                        double[] outputData = new double[outputFrames * 2]; // stereo
                        Marshal.Copy(outputPtr, outputData, 0, outputFrames * 2);
                        Console.WriteLine($"First output sample: {outputData[0]:F6}, {outputData[1]:F6}");
                    }
                }
                finally
                {
                    inputHandle.Free();
                }

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
