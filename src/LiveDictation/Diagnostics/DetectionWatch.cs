using System.IO;
using System.Text;
using LiveDictation.Audio;
using LiveDictation.Config;
using LiveDictation.Meetings;
using NAudio.CoreAudioApi;

namespace LiveDictation.Diagnostics;

/// <summary>
/// 会議検知が今なにを見ているかを、そのまま表示し続ける。
/// 「自動録音が始まらない」ときに、マイク側と再生側のどちらで落ちているかを切り分けるために使う。
/// LiveDictation.exe --watch [秒数] [--probe] で実行する。
/// --probe を付けると自分でマイクを開き、マイク検知そのものが動いているかを確かめられる。
/// </summary>
public static class DetectionWatch
{
    public static string LogPath { get; } = Path.Combine(AppSettings.AppDataRoot, "detection-watch.log");

    public static void Run(TimeSpan duration, bool probe = false)
    {
        Directory.CreateDirectory(AppSettings.AppDataRoot);
        using var file = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };

        void Emit(string line)
        {
            Console.WriteLine(line);
            file.WriteLine(line);
        }

        Emit($"会議検知の監視を開始します（{duration.TotalSeconds:0} 秒）。会議に参加して、表示の変化を見てください。");
        Emit($"記録先: {LogPath}");

        // 自分でマイクを掴んで、検知の仕組みそのものが動いていることを確認できるようにする。
        SourceCapture? microphoneProbe = null;
        if (probe)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                microphoneProbe = new SourceCapture(AudioSourceKind.Microphone, device);
                microphoneProbe.Start();
                Emit("検証用にマイクを開きました。以降 livedictation.exe がマイク使用中として見えるはずです。");
            }
            catch (Exception ex)
            {
                Emit($"検証用のマイクを開けませんでした: {ex.Message}");
            }
        }

        Emit(string.Empty);

        var deadline = DateTime.UtcNow + duration;
        string? previous = null;

        while (DateTime.UtcNow < deadline)
        {
            var snapshot = Capture();

            // 変化がないときに同じ行を流し続けても読みにくいので、変わったときだけ出す。
            if (!string.Equals(snapshot, previous, StringComparison.Ordinal))
            {
                Emit($"[{DateTime.Now:HH:mm:ss}] {snapshot}");
                previous = snapshot;
            }

            Thread.Sleep(1000);
        }

        microphoneProbe?.Dispose();

        Emit(string.Empty);
        Emit("監視を終了しました。");
    }

    private static string Capture()
    {
        var builder = new StringBuilder();

        // どちらの信号で拾えた（拾えなかった）かが分かるよう、内訳を分けて出す。
        var fromRegistry = MicrophoneUsageReader.GetFromConsentStore();
        var fromSession = AudioSessionReader.GetCapturing();

        var microphoneUsers = new HashSet<string>(fromRegistry, StringComparer.OrdinalIgnoreCase);
        microphoneUsers.UnionWith(fromSession);

        builder.Append("マイク[レジストリ]: ");
        builder.Append(Describe(fromRegistry));

        builder.Append(" / マイク[セッション]: ");
        builder.Append(Describe(fromSession));

        var playing = AudioSessionReader.GetPlaying();
        builder.Append(" / 再生: ");
        builder.Append(Describe(playing));

        builder.Append(" / 判定: ");
        builder.Append(Decide(microphoneUsers, playing));

        return builder.ToString();
    }

    private static string Describe(HashSet<string> values) =>
        values.Count == 0 ? "(なし)" : string.Join(", ", values.OrderBy(x => x));

    private static string Decide(HashSet<string> microphoneUsers, HashSet<string> playing)
    {
        foreach (var executable in microphoneUsers.OrderBy(x => x))
        {
            var app = MeetingApps.Match(executable);
            if (app is null)
            {
                continue;
            }

            if (playing.Contains(executable))
            {
                return $"会議 ({app.DisplayName} / 再生も確認)";
            }

            return $"開始条件を満たさず ({app.DisplayName} はマイクを使用中だが再生を検出できず)";
        }

        // 会議中は、マイクを手放していても再生が生きていれば継続とみなす。
        // ミュートで聞き役に回っただけで止まらないようにするための条件。
        foreach (var executable in playing)
        {
            var app = MeetingApps.Match(executable);
            if (app is not null)
            {
                return $"会議中なら継続 ({app.DisplayName} の再生あり / 開始条件は未達)";
            }
        }

        return "会議なし";
    }
}
