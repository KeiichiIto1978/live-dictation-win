using System.IO;
using LiveDictation.Diagnostics;

namespace LiveDictation.Recognition;

/// <summary>仕上げパスで使うオフラインモデル一式。</summary>
public sealed record OfflineModelPaths(string Directory, string Model, string Tokens, string ModelType);

/// <summary>
/// オフライン（非ストリーミング）モデルの置き場を探す。
/// ストリーミング用とはフォルダを分けて、取り違えを防ぐ。
/// </summary>
public static class OfflineModelLocator
{
    public static OfflineModelPaths Resolve(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"仕上げパス用のモデルフォルダが見つかりません: {directory}");
        }

        var tokens = Path.Combine(directory, "tokens.txt");
        if (!File.Exists(tokens))
        {
            throw new FileNotFoundException($"tokens.txt が見つかりません: {directory}", tokens);
        }

        // 量子化済みがあればそちらを使う。精度差より処理時間を優先する場面のため。
        var models = Directory.GetFiles(directory, "*.onnx")
            .OrderByDescending(path => path.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (models.Length == 0)
        {
            throw new FileNotFoundException($"モデル本体 (.onnx) が見つかりません: {directory}");
        }

        Log.Info($"仕上げパス用モデル: {Path.GetFileName(models[0])}");

        return new OfflineModelPaths(directory, models[0], tokens, "nemo_ctc");
    }

    /// <summary>設定が空のとき、models-offline 配下から最初に見つかった一式を使う。</summary>
    public static string? FindDefault(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var candidate in Directory.GetDirectories(root))
        {
            if (File.Exists(Path.Combine(candidate, "tokens.txt")) &&
                Directory.GetFiles(candidate, "*.onnx").Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }
}
