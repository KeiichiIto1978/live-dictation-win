using LiveDictation.Config;
using LiveDictation.Session;

namespace LiveDictation.Diagnostics;

/// <summary>
/// GUI を出さずに一定時間だけ録音し、そのまま文字起こしまで通す。
/// 会議を用意せずに本番と同じ経路を確かめるためのもの。
/// LiveDictation.exe --record [秒数] で実行する。
/// </summary>
public static class HeadlessRecorder
{
    public static bool Run(AppSettings settings, TimeSpan duration)
    {
        var session = new RecordingSession(settings, $"診断録音 {duration.TotalSeconds:0} 秒");

        Console.WriteLine($"{duration.TotalSeconds:0} 秒間録音します。何か話してみてください。");
        Console.WriteLine($"保存先: {session.Directory}");
        Console.WriteLine($"マイク: {(session.IsMicrophoneActive ? "OK" : "取得できず")} / 再生音: {(session.IsSystemPlaybackActive ? "OK" : "取得できず")}");
        Console.WriteLine();

        // 取り込めているかを数字で見せる。無反応なら音が入っていない。
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Console.WriteLine(
                $"  自分 {Bar(session.MicrophoneLevel)} {session.MicrophoneSeconds,5:0.0}s"
                + $"   相手 {Bar(session.SystemPlaybackLevel)} {session.SystemPlaybackSeconds,5:0.0}s");
            Thread.Sleep(1000);
        }

        var sessionDirectory = session.Directory;
        session.Dispose();

        Console.WriteLine();
        Console.WriteLine("録音を終了しました。");

        if (!settings.EnableFinalPass)
        {
            return true;
        }

        Console.WriteLine();
        return FinalPass.Run(settings, sessionDirectory, Console.WriteLine);
    }

    private static string Bar(float level)
    {
        const int width = 20;
        var filled = (int)Math.Round(Math.Clamp(level, 0f, 1f) * width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }
}
