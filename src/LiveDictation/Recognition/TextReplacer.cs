using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LiveDictation.Diagnostics;

namespace LiveDictation.Recognition;

/// <summary>
/// 認識結果に置換をかける。
///
/// 製品名や社内用語のような造語は、どの音声認識モデルも学習していないため原理的に当てられない。
/// 一方で誤り方は似通うので、利用者が「誤り → 正しい表記」を書けるようにしておく。
/// </summary>
public sealed class TextReplacer
{
    private sealed record Rule(Regex Pattern, string Replacement);

    private readonly Rule[] _rules;

    private TextReplacer(Rule[] rules) => _rules = rules;

    public static TextReplacer Empty { get; } = new([]);

    public int Count => _rules.Length;

    /// <summary>
    /// 1 行 1 ルール。`誤り => 正しい表記` の形式で書く。
    /// `#` で始まる行と空行は無視する。
    /// 左辺を `re:` で始めると正規表現として扱う。
    /// </summary>
    public static TextReplacer Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        var rules = new List<Rule>();

        try
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separator = line.IndexOf("=>", StringComparison.Ordinal);
                if (separator < 0)
                {
                    Log.Warn($"置換辞書の書式が不正なため無視しました: {line}");
                    continue;
                }

                var from = line[..separator].Trim();
                var to = line[(separator + 2)..].Trim();

                if (from.Length == 0)
                {
                    continue;
                }

                try
                {
                    var pattern = from.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
                        ? new Regex(from[3..].Trim(), RegexOptions.Compiled)
                        : new Regex(Regex.Escape(from), RegexOptions.Compiled);

                    rules.Add(new Rule(pattern, to));
                }
                catch (ArgumentException ex)
                {
                    Log.Warn($"置換辞書の正規表現が不正なため無視しました: {from} / {ex.Message}");
                }
            }
        }
        catch (IOException ex)
        {
            Log.Error($"置換辞書を読み込めませんでした: {path}", ex);
            return Empty;
        }

        if (rules.Count > 0)
        {
            Log.Info($"置換辞書を {rules.Count} 件読み込みました: {path}");
        }

        return new TextReplacer([.. rules]);
    }

    public string Apply(string text)
    {
        if (_rules.Length == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var rule in _rules)
        {
            text = rule.Pattern.Replace(text, rule.Replacement);
        }

        return text;
    }
}
