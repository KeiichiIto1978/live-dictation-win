using System.Text;

namespace LiveDictation.Recognition;

/// <summary>認識結果の文字列を読める形に整える。</summary>
public static class TextNormalizer
{
    /// <summary>
    /// モデルによっては日本語でも語間に半角スペースが混ざるため、
    /// 日本語の文字どうしに挟まれたスペースだけ畳んで読みやすくする。
    /// 英単語の区切りは残す必要があるので、両隣が日本語のときだけ落とす。
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            var current = raw[i];

            if (current != ' ')
            {
                builder.Append(current);
                continue;
            }

            var previous = builder.Length > 0 ? builder[^1] : '\0';
            var next = i + 1 < raw.Length ? raw[i + 1] : '\0';

            if (IsJapanese(previous) && IsJapanese(next))
            {
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 雑音に対してモデルが吐く短い断片を捨てる。
    /// 記号だけの行や、日本語を含まない 1〜2 文字の行は発言として意味を持たない。
    /// </summary>
    public static bool IsNoise(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.All(c => !char.IsLetterOrDigit(c)))
        {
            return true;
        }

        if (trimmed.Length <= 2 && !trimmed.Any(IsJapanese))
        {
            return true;
        }

        return false;
    }

    private static bool IsJapanese(char value) =>
        value is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '＀' and <= '￯';
}
