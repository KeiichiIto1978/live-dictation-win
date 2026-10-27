using System.IO;
using System.Text;

namespace LiveDictation.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// ファイルとデバッグ出力に書く最小限のロガー。
/// 音声コールバックからも呼ばれるため、書き込みはロックで直列化する。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static event Action<LogLevel, string>? Logged;

    public static void Initialize(string appDataRoot)
    {
        lock (Gate)
        {
            var dir = Path.Combine(appDataRoot, "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, $"live-dictation-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message} / {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-7}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (Gate)
        {
            if (_logPath is not null)
            {
                try
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // ログが書けないこと自体でアプリを止めない。
                }
            }
        }

        Logged?.Invoke(level, message);
    }
}
