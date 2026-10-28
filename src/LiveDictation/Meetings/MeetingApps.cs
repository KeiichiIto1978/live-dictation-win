namespace LiveDictation.Meetings;

/// <summary>会議アプリの定義。mac 版のバンドル ID 判定を Windows の実行ファイル名に置き換えたもの。</summary>
public sealed record MeetingApp(string DisplayName, string[] Executables);

public static class MeetingApps
{
    /// <summary>
    /// 会議として扱うアプリ。
    ///
    /// 以前はブラウザだけ「再生も出ていること」を追加条件にしていたが、アプリ種別で
    /// 条件を分けるのをやめた。会議中にスピーカーを使わないアプリは実際には無く、
    /// 終了判定ではもともと全アプリの再生を見ているため、開始だけ条件を変える理由がない。
    /// </summary>
    public static readonly IReadOnlyList<MeetingApp> Known =
    [
        new("Zoom", ["zoom.exe", "zoommtg.exe"]),
        new("Microsoft Teams", ["teams.exe", "ms-teams.exe", "msteams.exe"]),
        new("Webex", ["ciscocollabhost.exe", "webexmta.exe", "webex.exe", "atmgr.exe"]),
        new("Slack", ["slack.exe"]),
        new("Discord", ["discord.exe", "discordptb.exe", "discordcanary.exe"]),
        new("Google Chrome", ["chrome.exe"]),
        new("Microsoft Edge", ["msedge.exe"]),
        new("Firefox", ["firefox.exe"]),
    ];

    /// <summary>
    /// マイクを掴むが会議ではないもの。これを弾かないと音声入力ツールで録音が始まってしまう。
    /// </summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "livedictation.exe",
        "voiceaccess.exe",
        "ctfmon.exe",
        "searchapp.exe",
        "speechruntime.exe",
        "audiodg.exe",
    };

    public static MeetingApp? Match(string executableName)
    {
        if (Excluded.Contains(executableName))
        {
            return null;
        }

        foreach (var app in Known)
        {
            foreach (var candidate in app.Executables)
            {
                if (string.Equals(candidate, executableName, StringComparison.OrdinalIgnoreCase))
                {
                    return app;
                }
            }
        }

        return null;
    }
}
