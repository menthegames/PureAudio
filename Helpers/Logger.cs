using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace PureAudio.Helpers;

/// <summary>
/// Centralized logger that writes to both Debug output and a log file (output.log).
/// The log file is located in the application's base directory.
/// 
/// Features:
/// - Automatic log rotation: if log file exceeds 10 MB, it's renamed to output.old
/// - Conditional compilation: in DEBUG builds, all messages are logged.
///   In RELEASE builds, only errors and warnings are logged (use LogWarning, LogError).
/// - Thread-safe via lock.
/// </summary>
public static class Logger
{
    private static readonly string LogFilePath;
    private static readonly string OldLogFilePath;
    private static readonly object LockObj = new();
    private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

    static Logger()
    {
        LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output.log");
        OldLogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output.old");

        // Rotate existing log if too large, then clear
        try
        {
            CheckLogSize();
        }
        catch
        {
            // Ignore errors during initialization
        }
    }

    /// <summary>
    /// Checks if the log file exceeds the maximum size and rotates it if necessary.
    /// </summary>
    public static void CheckLogSize()
    {
        lock (LockObj)
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > MaxLogSizeBytes)
                    {
                        // Rename current log to output.old (overwrite existing .old)
                        if (File.Exists(OldLogFilePath))
                            File.Delete(OldLogFilePath);
                        File.Move(LogFilePath, OldLogFilePath);
                    }
                }
            }
            catch
            {
                // Ignore file errors during rotation
            }
        }
    }

    /// <summary>
    /// Writes a message to both Debug output and the log file.
    /// In RELEASE builds, this method is stripped by the compiler
    /// when called from [Conditional("DEBUG")] contexts.
    /// Use this for general diagnostic messages.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Log(string message, [CallerFilePath] string? callerFile = null, [CallerMemberName] string? callerMember = null)
    {
        WriteLog("INFO", message, callerFile, callerMember);
    }

    /// <summary>
    /// Writes a warning message. Logged in both DEBUG and RELEASE builds.
    /// </summary>
    public static void LogWarning(string message, [CallerFilePath] string? callerFile = null, [CallerMemberName] string? callerMember = null)
    {
        WriteLog("WARN", message, callerFile, callerMember);
    }

    /// <summary>
    /// Writes an error message. Logged in both DEBUG and RELEASE builds.
    /// </summary>
    public static void LogError(string message, [CallerFilePath] string? callerFile = null, [CallerMemberName] string? callerMember = null)
    {
        WriteLog("ERROR", message, callerFile, callerMember);
    }

    /// <summary>
    /// Writes an exception to both Debug output and the log file.
    /// Logged in both DEBUG and RELEASE builds.
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

    /// <summary>
    /// Internal write method. Always writes to Debug output.
    /// In RELEASE builds, only writes to file for WARN, ERROR, and EXCEPTION levels.
    /// </summary>
    private static void WriteLog(string level, string message, string? callerFile, string? callerMember)
    {
        string caller = !string.IsNullOrEmpty(callerFile)
            ? $"[{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}]"
            : string.Empty;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string formattedMessage = $"{timestamp} {caller} {level}: {message}";

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
}
