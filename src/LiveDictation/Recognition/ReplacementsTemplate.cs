using System.IO;
using System.Text;
using LiveDictation.Config;

namespace LiveDictation.Recognition;

/// <summary>置換辞書のひな形。初回に書き方の説明ごと置いておく。</summary>
public static class ReplacementsTemplate
{
    private const string Template = """
# 置換辞書
#
# 製品名や社内用語のような造語は、どの音声認識モデルも学習していないため
# そのままでは当てられません。実際に出てきた誤りをここに書いて直してください。
#
# 書き方: 誤り => 正しい表記
#   - 1 行に 1 つ
#   - # で始まる行と空行は無視されます
#   - 左辺を re: で始めると正規表現になります（置換後で $1 が使えます）
#
# 書き換えは次回の録音開始時から有効になります。
#
# 例:
# 仕分けたー => Shiwaketter
# re:ポカ ?ミル => ポカミル
#
# 取りこぼしを減らす設定にしている都合で、不明瞭な音に対して
# 多言語モデルが別言語の文字や記号を出すことがあります。
# 空に置換すると消せます（=> の右を書かない）。
re:^[♪?？!！]+$ =>
那么 =>
我 =>

""";

    public static string EnsureExists()
    {
        var path = AppSettings.ReplacementsPath;

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(AppSettings.AppDataRoot);
            File.WriteAllText(path, Template, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        return path;
    }
}
