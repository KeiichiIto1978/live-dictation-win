using System.Diagnostics;
using System.IO;
using System.Text;
using LiveDictation.Audio;
using LiveDictation.Config;
using LiveDictation.Diagnostics;
using LiveDictation.Recognition;

namespace LiveDictation.Session;

/// <summary>
/// 録音し終わった音声を、日本語専用のオフラインモデルで通しで認識し直す。
///
/// ライブ表示はストリーミング認識が担うが、そちらは先の音を知らないまま判断するため
/// 日本語の変換が弱い。会議が終わったあとなら時間の制約がないので、
/// 発話全体を見られるオフラインモデルで作り直したほうが読める文章になる。
/// </summary>
public static class FinalPass
{
    public const string FinalTranscriptFileName = "transcript.md";

    public static bool IsAvailable(AppSettings settings) => TryResolve(settings, out _, out _, out _);

    private static bool TryResolve(
        AppSettings settings,
        out OfflineModelPaths? model,
        out string vadPath,
        out string reason)
    {
        model = null;
        vadPath = string.Empty;

        var directory = settings.OfflineModelDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = OfflineModelLocator.FindDefault(settings.DefaultOfflineModelRoot) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            reason = "仕上げパス用のモデルが見つかりません。scripts フォルダの download-offline-model.ps1 を実行してください。";
            return false;
        }

        vadPath = string.IsNullOrWhiteSpace(settings.VadModelPath)
            ? Path.Combine(settings.DefaultOfflineModelRoot, "silero_vad.onnx")
            : settings.VadModelPath;

        if (!File.Exists(vadPath))
        {
            reason = $"VAD モデルが見つかりません: {vadPath}";
            return false;
        }

        try
        {
            model = OfflineModelLocator.Resolve(directory);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// セッションフォルダの音声を認識し直して transcript.md を作る。
    /// report には進捗の 1 行を渡す（コマンドラインからの実行時に表示する用）。
    /// </summary>
    public static bool Run(
        AppSettings settings,
        string sessionDirectory,
        Action<string>? report = null,
        Action<double>? progress = null)
    {
        void Say(string line)
        {
            Log.Info(line);
            report?.Invoke(line);
        }

        // 単体の音声ファイルを渡されたときは、それだけを文字起こしする。
        // 会議の録音に限らず、手元のファイルを処理したい場面があるため。
        if (File.Exists(sessionDirectory))
        {
            return RunForFile(settings, sessionDirectory, Say, report, progress);
        }

        if (!Directory.Exists(sessionDirectory))
        {
            Say($"セッションフォルダが見つかりません: {sessionDirectory}");
            return false;
        }

        if (!TryResolve(settings, out var model, out var vadPath, out var reason))
        {
            Say(reason);
            return false;
        }

        var sources = new (AudioSourceKind Kind, string Path)[]
        {
            (AudioSourceKind.Microphone, Path.Combine(sessionDirectory, "microphone.wav")),
            (AudioSourceKind.SystemPlayback, Path.Combine(sessionDirectory, "system.wav")),
        };

        var clock = Stopwatch.StartNew();
        var segments = new List<TranscriptSegment>();
        var audioSeconds = 0.0;

        // 進捗を全体比で出すため、処理する系統の数を先に数える。
        var totalSources = sources.Count(source =>
            File.Exists(source.Path) && new FileInfo(source.Path).Length > 64);
        var completedSources = 0;

        try
        {
            using var transcriber = new OfflineTranscriber(model!, vadPath, settings);

            foreach (var (kind, path) in sources)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var length = new FileInfo(path).Length;
                if (length <= 64)
                {
                    // ヘッダーだけの WAV。何も鳴っていなかった側。
                    continue;
                }

                Say($"{kind.ToDisplayName()}の音声を認識中...");

                var lastReported = -1;
                var sourceIndex = completedSources;
                var found = transcriber.Transcribe(path, kind, fraction =>
                {
                    // 2 系統あるので、全体に対する割合に直して渡す。
                    progress?.Invoke((sourceIndex + fraction) / Math.Max(1, totalSources));

                    var percent = (int)(fraction * 100) / 10 * 10;
                    if (percent > lastReported)
                    {
                        lastReported = percent;
                        report?.Invoke($"{kind.ToDisplayName()}の音声を認識中 {percent}%");
                    }
                });

                completedSources++;

                segments.AddRange(found);
                audioSeconds += (length - 44) / 2.0 / SourceCapture.TargetSampleRate;
            }
        }
        catch (Exception ex)
        {
            Log.Error("仕上げパスに失敗しました", ex);
            Say($"仕上げパスに失敗しました: {ex.Message}");
            return false;
        }

        // 話者が別々に認識されるため、時刻順に並べ直して 1 本の会話に戻す。
        segments.Sort((left, right) => left.Start.CompareTo(right.Start));

        Write(sessionDirectory, segments, clock.Elapsed, audioSeconds, model!);

        var rtf = audioSeconds > 0 ? clock.Elapsed.TotalSeconds / audioSeconds : 0;
        Say($"仕上げパスが完了しました。発言 {segments.Count} 件 / 音声 {audioSeconds:0} 秒 / 処理 {clock.Elapsed.TotalSeconds:0} 秒（実時間比 {rtf:0.00}）");
        return true;
    }

    /// <summary>音声ファイル 1 本を文字起こしし、同じ場所に Markdown を書き出す。</summary>
    private static bool RunForFile(
        AppSettings settings,
        string audioPath,
        Action<string> say,
        Action<string>? report,
        Action<double>? progress)
    {
        if (!TryResolve(settings, out var model, out var vadPath, out var reason))
        {
            say(reason);
            return false;
        }

        var clock = Stopwatch.StartNew();
        List<TranscriptSegment> segments;

        try
        {
            using var transcriber = new OfflineTranscriber(model!, vadPath, settings);

            say($"認識中: {Path.GetFileName(audioPath)}");

            var lastReported = -1;
            segments = transcriber.Transcribe(audioPath, AudioSourceKind.Microphone, fraction =>
            {
                progress?.Invoke(fraction);

                var percent = (int)(fraction * 100) / 10 * 10;
                if (percent > lastReported)
                {
                    lastReported = percent;
                    report?.Invoke($"  {percent}%");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("ファイルの仕上げパスに失敗しました", ex);
            say($"失敗しました: {ex.Message}");
            return false;
        }

        var outputPath = Path.ChangeExtension(audioPath, null) + "-transcript.md";
        var builder = new StringBuilder();
        builder.AppendLine($"# 文字起こし {Path.GetFileName(audioPath)}");
        builder.AppendLine();
        builder.AppendLine($"- 作成: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- モデル: {Path.GetFileName(model!.Directory)}");
        builder.AppendLine();
        builder.AppendLine("## 発言ログ");
        builder.AppendLine();

        foreach (var segment in segments)
        {
            builder.AppendLine(segment.ToMarkdownLine());
            report?.Invoke(segment.ToMarkdownLine());
        }

        File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        say($"完了しました。発言 {segments.Count} 件 / 処理 {clock.Elapsed.TotalSeconds:0.0} 秒");
        say($"出力: {outputPath}");
        return true;
    }

    private static void Write(
        string sessionDirectory,
        List<TranscriptSegment> segments,
        TimeSpan elapsed,
        double audioSeconds,
        OfflineModelPaths model)
    {
        var path = Path.Combine(sessionDirectory, FinalTranscriptFileName);
        var builder = new StringBuilder();

        builder.AppendLine($"# 文字起こし {Path.GetFileName(sessionDirectory)}");
        builder.AppendLine();
        builder.AppendLine($"- 仕上げパス: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- モデル: {Path.GetFileName(model.Directory)}");
        builder.AppendLine($"- 音声 {audioSeconds:0} 秒 / 処理 {elapsed.TotalSeconds:0} 秒");
        builder.AppendLine();
        builder.AppendLine("## 発言ログ");
        builder.AppendLine();

        if (segments.Count == 0)
        {
            builder.AppendLine("(発話として認識できたものがありません)");
        }

        foreach (var segment in segments)
        {
            builder.AppendLine(segment.ToMarkdownLine());
        }

        // 録音中に書いたものは残す。比較したいときと、仕上げパスが失敗したときの保険になる。
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
