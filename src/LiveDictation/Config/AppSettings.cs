using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveDictation.Diagnostics;

namespace LiveDictation.Config;

/// <summary>
/// アプリ設定。%LOCALAPPDATA%\LiveDictation\settings.json に保存する。
/// </summary>
public sealed class AppSettings
{
    /// <summary>ONNX Runtime の実行プロバイダ。cpu / cuda / directml。</summary>
    public string Provider { get; set; } = "cpu";

    /// <summary>会議アプリを検知したら自動で録音を開始するか。</summary>
    public bool AutoRecordEnabled { get; set; } = true;

    /// <summary>会議アプリがマイクを手放してから録音を止めるまでの猶予秒数。ミュートで細切れにしないための値。</summary>
    public int MeetingGraceSeconds { get; set; } = 60;

    /// <summary>
    /// 会議中は、マイクを手放していても相手の声が鳴っていれば継続とみなすか。
    ///
    /// ミュートして聞き役に回っただけで録音が止まるのを防ぐための設定。
    /// 会議を抜けたあとに同じアプリで動画などを再生していると録音が止まらないが、
    /// 会議の取りこぼしのほうが損失が大きいため有効を既定とする。
    /// 止まらないほうが困る場合は false にする。
    /// </summary>
    public bool KeepMeetingAliveWhilePlaying { get; set; } = true;

    /// <summary>手動録音の自動停止までの時間。止め忘れ対策。</summary>
    public double MaxRecordingHours { get; set; } = 3.0;






    /// <summary>セッションの保存先。空なら %LOCALAPPDATA%\LiveDictation\Sessions。</summary>
    public string SessionRoot { get; set; } = "";

    /// <summary>録音停止後に、日本語専用モデルで文字起こしを作り直すか。</summary>
    public bool EnableFinalPass { get; set; } = true;

    /// <summary>仕上げパス用のオフラインモデルのフォルダ。空なら models-offline 配下を探す。</summary>
    public string OfflineModelDirectory { get; set; } = "";

    /// <summary>VAD モデルのパス。空なら models-offline\silero_vad.onnx。</summary>
    public string VadModelPath { get; set; } = "";

    /// <summary>仕上げパスのスレッド数。会議後に動くため、ライブより多く使ってよい。</summary>
    public int OfflineNumThreads { get; set; } = 8;

    /// <summary>発話とみなす確信度。上げると拾いが減り、下げると雑音を拾う。</summary>
    public double VadThreshold { get; set; } = 0.5;

    /// <summary>発話の切れ目とみなす無音の長さ（秒）。</summary>
    public double VadMinSilenceSeconds { get; set; } = 0.5;

    /// <summary>1 発話の上限（秒）。既定の 5 秒では会議の発話が途中で切れる。</summary>
    public double VadMaxSpeechSeconds { get; set; } = 20.0;

    /// <summary>
    /// これより静かな区間は文字起こしにかけない（dBFS、0 が最大音量）。
    ///
    /// 鳴っていない区間にモデルが文章を作ってしまうため、入力の時点で落とす。
    /// 実会議で測ったところ、本当の発話は最小でも -46.6 dBFS、
    /// 文章が捏造された区間はすべて -56 dBFS 以下で、間が 10 dB 空いていた。
    /// 下げると捏造が通り、上げると小声の相づちが消える。
    /// </summary>
    public double SilenceGateDbfs { get; set; } = -55.0;

    [JsonIgnore]
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveDictation");

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(AppDataRoot, "settings.json");

    [JsonIgnore]
    public string ResolvedSessionRoot =>
        string.IsNullOrWhiteSpace(SessionRoot) ? Path.Combine(AppDataRoot, "Sessions") : SessionRoot;

    [JsonIgnore]
    public string DefaultOfflineModelRoot => Path.Combine(AppDataRoot, "models-offline");

    /// <summary>
    /// モデルを探す場所。前から順に見る。
    /// 実行ファイルの親をたどるのは、リポジトリの models/ に置いたまま使えるようにするため。
    /// 構成や発行方法で実行ファイルの深さが変わるので、段数を決め打ちせずドライブ直下まで見る。
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> ModelSearchRoots
    {
        get
        {
            yield return DefaultOfflineModelRoot;

            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                yield return Path.Combine(directory.FullName, "models");
            }
        }
    }

    /// <summary>造語・製品名を直すための置換辞書。</summary>
    [JsonIgnore]
    public static string ReplacementsPath { get; } = Path.Combine(AppDataRoot, "replacements.txt");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 設定が壊れていても起動は止めない。既定値で続行し、保存時に上書きする。
            Log.Warn($"設定ファイルを読み込めなかったため既定値を使います: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataRoot);
        // 書き込み途中で落ちても設定を失わないよう、一時ファイル経由で差し替える。
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }
}
