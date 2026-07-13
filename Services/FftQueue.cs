using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using PureAudio.Helpers;

namespace PureAudio.Services;

/// <summary>
/// Thread-safe queue for FFT data processing.
/// 
/// Audio threads (BitPerfectWaveProvider, FftSampleProvider) push float sample arrays
/// into the queue without blocking. A dedicated background thread consumes the queue
/// and calls FftService.ProcessSamples() at a controlled rate (~15ms intervals).
/// 
/// This prevents FFT computation from blocking the audio playback thread,
/// eliminating stuttering in Exclusive mode (especially for 24/96 high-res files).
/// </summary>
internal class FftQueue : IDisposable
{
    private readonly ConcurrentQueue<(float[] array, int length)> _queue = new();
    private readonly FftService _fftService;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _workerThread;
    private bool _disposed;

    // Minimum interval between FFT processing calls (in ms).
    // 15ms = ~66 FPS, which is more than enough for a 30 FPS spectrum display.
    // This prevents CPU overload from excessive FFT computations.
    private const int ProcessingIntervalMs = 15;

    public FftQueue(FftService fftService)
    {
        _fftService = fftService;

        _workerThread = new Thread(ProcessQueue)
        {
            Name = "FFT Queue Processor",
            // Low priority so it never steals CPU from the audio thread
            Priority = ThreadPriority.BelowNormal,
            IsBackground = true
        };
        _workerThread.Start();

        Logger.Log("FftQueue: worker thread started");
    }

    /// <summary>
    /// Called by audio threads to enqueue float sample data for FFT processing.
    /// This is very fast (lock-free enqueue) and does NOT block the audio thread.
    /// </summary>
    public void Enqueue(float[] samples, int length)
    {
        if (_disposed) return;

        // Rent from ArrayPool to reduce GC pressure
        // Note: ArrayPool.Rent may return an array larger than requested,
        // so we store the actual length separately.
        float[] copy = ArrayPool<float>.Shared.Rent(length);
        System.Array.Copy(samples, copy, length);
        _queue.Enqueue((copy, length));
    }

    /// <summary>
    /// Background thread that drains the queue and processes FFT at a controlled rate.
    /// </summary>
    private void ProcessQueue()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Process all available items in the queue, but at most once per interval
                while (_queue.TryDequeue(out var item))
                {
                    var (samples, length) = item;
                    if (samples != null && length > 0)
                    {
                        _fftService.ProcessSamples(samples.AsSpan(0, length));
                    }

                    // Return the rented array back to the pool
                    if (samples != null)
                    {
                        ArrayPool<float>.Shared.Return(samples);
                    }
                }

                // Sleep for the processing interval to limit CPU usage
                // Using Sleep instead of a timer to keep it simple and avoid timer drift
                _cts.Token.WaitHandle.WaitOne(ProcessingIntervalMs);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Log($"FftQueue: worker thread exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all pending FFT data from the queue.
    /// Called when seeking or changing tracks to discard stale data.
    /// </summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out var item))
        {
            var (samples, _) = item;
            if (samples != null)
            {
                ArrayPool<float>.Shared.Return(samples);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_workerThread.IsAlive)
        {
            if (!_workerThread.Join(500))
            {
                Logger.Log("FftQueue: worker thread did not stop in time");
            }
        }

        _cts.Dispose();
        Clear();
    }
}
