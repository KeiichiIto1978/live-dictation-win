using System.IO;
using System.Text;
using LiveDictation.Config;
using LiveDictation.Session;
using NAudio.CoreAudioApi;

namespace LiveDictation.Diagnostics;

/// <summary>
/// 会議の直前に「録れるはずだったのに録れていない」と気づくのを避けるための事前確認。
/// 音声デバイスと文字起こしモデルの有無だけを調べ、結果をファイルに書いて終わる。
/// LiveDictation.exe --selftest で実行する。
/// </summary>
public static class SelfTest
{
    public static string ResultPath { get; } = Path.Combine(AppSettings.AppDataRoot, "selftest.txt");

    public static bool Run(AppSettings settings)
    {
        var report = new StringBuilder();
        var succeeded = true;

        report.AppendLine($"自己診断 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        succeeded &= CheckAudioDevices(report);
        succeeded &= CheckTranscription(settings, report);

        report.AppendLine();
        report.AppendLine(succeeded ? "結果: 使用できます" : "結果: 問題があります");

        Directory.CreateDirectory(AppSettings.AppDataRoot);
        File.WriteAllText(ResultPath, report.ToString(), new UTF8Encoding(false));

        Console.Write(report.ToString());
        Log.Info($"自己診断を書き出しました: {ResultPath}");
        return succeeded;
    }

    private static bool CheckAudioDevices(StringBuilder report)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            report.AppendLine($"[OK] 再生デバイス（相手の声）: {render.FriendlyName}");

            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            report.AppendLine($"[OK] マイク（自分の声）: {capture.FriendlyName}");

            return true;
        }
        catch (Exception ex)
        {
            report.AppendLine($"[NG] 音声デバイスを取得できません: {ex.Message}");
            return false;
        }
    }

    private static bool CheckTranscription(AppSettings settings, StringBuilder report)
    {
        if (!settings.EnableFinalPass)
        {
            report.AppendLine("[--] 文字起こしは設定で無効になっています（録音のみ）");
            return true;
        }

        if (!FinalPass.IsAvailable(settings))
        {
            report.AppendLine("[NG] 文字起こし用のモデルが見つかりません");
            report.AppendLine("     scripts フォルダの download-offline-model.ps1 を実行してください");
            return false;
        }

        report.AppendLine($"[OK] 文字起こしモデル: {Path.Combine(settings.DefaultOfflineModelRoot)}");
        return true;
    }
}
