using System.IO;
using LiveDictation.Diagnostics;

namespace LiveDictation.Recognition;

/// <summary>sherpa-onnx が扱えるオフラインモデルの種別。</summary>
public enum OfflineModelKind
{
    /// <summary>NeMo の CTC モデル。単一ファイル。</summary>
    NemoCtc,

    /// <summary>encoder / decoder / joiner の 3 点で構成される transducer。</summary>
    Transducer,

    /// <summary>SenseVoice。単一ファイル。</summary>
    SenseVoice,

    /// <summary>Moonshine。encoder と decoder で構成される。</summary>
    Moonshine,

    /// <summary>
    /// FunASR Nano。音声エンコーダの出力を小型 LLM に渡して書き起こす。
    /// tokens.txt を持たず、トークナイザーをフォルダごと渡す点がほかと違う。
    /// </summary>
    FunAsrNano,
}

/// <summary>
/// 仕上げパスで使うオフラインモデル一式。
///
/// FunASR Nano は 3 つのモデル本体を使うため、既存の器を次のように使い回している。
/// Encoder = encoder_adaptor / Decoder = llm / Model = embedding。
/// </summary>
public sealed record OfflineModelPaths(
    string Directory,
    OfflineModelKind Kind,
    string Tokens,
    string Model = "",
    string Encoder = "",
    string Decoder = "",
    string Joiner = "",
    string Tokenizer = "")
{
    public string Name => Path.GetFileName(Directory);
}

/// <summary>
/// オフラインモデルの置き場を探し、種別を見分ける。
///
/// 配布されているモデルはファイル構成がばらばらなので、
/// 利用者に種別を設定させず、入っているファイルから判定する。
/// </summary>
public static class OfflineModelLocator
{
    /// <summary>ONNX Runtime が読める拡張子。Moonshine 系は .ort で配布されている。</summary>
    private static readonly string[] ModelExtensions = [".onnx", ".ort"];

    public static OfflineModelPaths Resolve(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"モデルフォルダが見つかりません: {directory}");
        }

        // FunASR Nano だけは tokens.txt を持たない。
        // 先に見分けておかないと、この直後の確認で弾いてしまう。
        var funAsrNano = DetectFunAsrNano(directory);
        if (funAsrNano is not null)
        {
            Log.Info($"モデルを認識しました: {funAsrNano.Name} ({funAsrNano.Kind})");
            return funAsrNano;
        }

        var tokens = Path.Combine(directory, "tokens.txt");
        if (!File.Exists(tokens))
        {
            throw new FileNotFoundException($"tokens.txt が見つかりません: {directory}", tokens);
        }

        var files = ModelFiles(directory);

        if (files.Length == 0)
        {
            throw new FileNotFoundException($"モデル本体が見つかりません: {directory}");
        }

        var resolved = Detect(directory, tokens, files);
        Log.Info($"モデルを認識しました: {resolved.Name} ({resolved.Kind})");
        return resolved;
    }

    private static OfflineModelPaths Detect(string directory, string tokens, string[] files)
    {
        // Moonshine は encoder と「まとめられた decoder」の 2 点。先に見分ける。
        var moonshineEncoder = Pick(files, "encoder_model");
        var moonshineDecoder = Pick(files, "decoder_model_merged");
        if (moonshineEncoder is not null && moonshineDecoder is not null)
        {
            return new OfflineModelPaths(
                directory, OfflineModelKind.Moonshine, tokens,
                Encoder: moonshineEncoder, Decoder: moonshineDecoder);
        }

        // transducer は 3 点そろっている。
        var encoder = Pick(files, "encoder");
        var decoder = Pick(files, "decoder");
        var joiner = Pick(files, "joiner");
        if (encoder is not null && decoder is not null && joiner is not null)
        {
            return new OfflineModelPaths(
                directory, OfflineModelKind.Transducer, tokens,
                Encoder: encoder, Decoder: decoder, Joiner: joiner);
        }

        // ここから先は単一ファイル。ファイル名では区別できないため、フォルダ名で判断する。
        var model = files.OrderByDescending(IsQuantized).First();
        var name = Path.GetFileName(directory);

        var kind = name.Contains("sense", StringComparison.OrdinalIgnoreCase)
            ? OfflineModelKind.SenseVoice
            : OfflineModelKind.NemoCtc;

        return new OfflineModelPaths(directory, kind, tokens, Model: model);
    }

    /// <summary>
    /// FunASR Nano かどうかを、3 つのモデル本体とトークナイザーのフォルダで見分ける。
    /// 条件を満たさないときは null を返し、ほかの種別の判定に任せる。
    /// </summary>
    private static OfflineModelPaths? DetectFunAsrNano(string directory)
    {
        var files = ModelFiles(directory);
        var encoderAdaptor = Pick(files, "encoder_adaptor");
        var llm = Pick(files, "llm");
        var embedding = Pick(files, "embedding");

        if (encoderAdaptor is null || llm is null || embedding is null)
        {
            return null;
        }

        // トークナイザーは vocab.json を持つサブフォルダ。配布物では Qwen3-0.6B という名前。
        var tokenizer = Directory.GetDirectories(directory)
            .FirstOrDefault(found => File.Exists(Path.Combine(found, "vocab.json")));

        if (tokenizer is null)
        {
            return null;
        }

        return new OfflineModelPaths(
            directory, OfflineModelKind.FunAsrNano, Tokens: "",
            Model: embedding, Encoder: encoderAdaptor, Decoder: llm, Tokenizer: tokenizer);
    }

    private static string[] ModelFiles(string directory) =>
        Directory.GetFiles(directory)
            .Where(path => ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>量子化済み (int8) があればそちらを優先する。</summary>
    private static bool IsQuantized(string path) =>
        path.Contains(".int8.", StringComparison.OrdinalIgnoreCase);

    private static string? Pick(string[] files, string role) =>
        files
            .Where(path => Path.GetFileName(path).StartsWith(role, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(IsQuantized)
            .FirstOrDefault();

    /// <summary>
    /// 設定が空のとき、指定フォルダ配下から使えるモデルを 1 つ選ぶ。
    ///
    /// 複数置かれていることがある（比較のために入れ替える、古いものが残っている）ため、
    /// **最後に入れたもの**を選ぶ。取得スクリプトを実行したら、それが使われる。
    /// </summary>
    public static string? FindDefault(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var candidates = Directory.GetDirectories(root)
            .OrderByDescending(Directory.GetLastWriteTimeUtc);

        foreach (var candidate in candidates)
        {
            if (LooksLikeModel(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// そのフォルダが仕上げパスで使えるモデルかどうか。
    /// FunASR Nano は tokens.txt を持たないため、別に見る必要がある。
    /// </summary>
    private static bool LooksLikeModel(string directory) =>
        DetectFunAsrNano(directory) is not null
        || (File.Exists(Path.Combine(directory, "tokens.txt")) && ModelFiles(directory).Length > 0);
}
