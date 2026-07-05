using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace PureAudio.Helpers;

/// <summary>
/// Centralized logger that writes to both Debug output and a log file (output.log).
/// The log file is located in the application's base directory.
/// </summary>
public static class Logger
{
    private static readonly string LogFilePath;
    private static readonly object LockObj = new();

    static Logger()
    {
        LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output.log");
        // Clear log file on startup
        try
        {
            if (File.Exists(LogFilePath))
                File.Delete(LogFilePath);
        }
        catch
        {
            // Ignore errors during initialization
        }
    }

    /// <summary>
    /// Writes a message to both Debug output and the log file.
    /// </summary>
    public static void Log(string message, [CallerFilePath] string? callerFile = null, [CallerMemberName] string? callerMember = null)
    {
        string caller = !string.IsNullOrEmpty(callerFile)
            ? $"[{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}]"
            : string.Empty;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string formattedMessage = $"{timestamp} {caller} {message}";

        Debug.WriteLine(formattedMessage);

        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, formattedMessage + Environment.NewLine);
            }
            catch
            {
                // Ignore file write errors (e.g., file locked)
            }
        }
    }

    /// <summary>
    /// Writes an exception to both Debug output and the log file.
    /// </summary>
    public static void LogException(Exception ex, string context = "", [CallerFilePath] string? callerFile = null, [CallerMemberName] string? callerMember = null)
    {
        string caller = !string.IsNullOrEmpty(callerFile)
            ? $"[{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}]"
            : string.Empty;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string formattedMessage = $"{timestamp} {caller} EXCEPTION: {context} {ex.GetType().Name}: {ex.Message}";

        Debug.WriteLine(formattedMessage);
        Debug.WriteLine($"{timestamp} {caller} Stack: {ex.StackTrace}");

        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, formattedMessage + Environment.NewLine);
                File.AppendAllText(LogFilePath, $"{timestamp} {caller} Stack: {ex.StackTrace}" + Environment.NewLine);
            }
            catch
            {
                // Ignore file write errors
            }
        }
    }
}
