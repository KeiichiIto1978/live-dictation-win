namespace LiveDictation.Meetings;

/// <summary>会議アプリの定義。mac 版のバンドル ID 判定を Windows の実行ファイル名に置き換えたもの。</summary>
public sealed record MeetingApp(string DisplayName, string[] Executables, bool RequiresPlayback);

public static class MeetingApps
{
    /// <summary>
    /// ブラウザは「マイクを使っている」だけでは会議と断定できない（録音サイトや音声入力でも鳴る）。
    /// mac 版と同じく、再生も同時に出ている場合だけ会議とみなす。
    /// </summary>
    public static readonly IReadOnlyList<MeetingApp> Known =
    [
        new("Zoom", ["zoom.exe", "zoommtg.exe"], RequiresPlayback: false),
        new("Microsoft Teams", ["teams.exe", "ms-teams.exe", "msteams.exe"], RequiresPlayback: false),
        new("Webex", ["ciscocollabhost.exe", "webexmta.exe", "webex.exe", "atmgr.exe"], RequiresPlayback: false),
        new("Slack", ["slack.exe"], RequiresPlayback: false),
        new("Discord", ["discord.exe", "discordptb.exe", "discordcanary.exe"], RequiresPlayback: false),
        new("Google Chrome", ["chrome.exe"], RequiresPlayback: true),
        new("Microsoft Edge", ["msedge.exe"], RequiresPlayback: true),
        new("Firefox", ["firefox.exe"], RequiresPlayback: true),
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
