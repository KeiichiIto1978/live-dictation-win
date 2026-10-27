using Microsoft.Win32;

namespace LiveDictation.Meetings;

/// <summary>
/// どのアプリが今マイクを使っているかを読む。
///
/// Windows はマイクの使用開始・終了を ConsentStore に記録しており、
/// LastUsedTimeStop が 0 のあいだは「使用中」を意味する。
/// マイクデバイス全体の状態ではなくアプリ単位で分かるため、mac 版の
/// kAudioProcessPropertyIsRunningInput に相当する判定ができる。
/// </summary>
public static class MicrophoneUsageReader
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>現在マイクを使用中の実行ファイル名を返す。</summary>
    public static HashSet<string> GetExecutablesInUse()
    {
        var result = GetFromConsentStore();

        // ConsentStore に記録が出ないアプリ・環境があるため、
        // オーディオエンジン側の録音セッションも併せて見る。どちらかで拾えれば検知できる。
        result.UnionWith(AudioSessionReader.GetCapturing());

        return result;
    }

    /// <summary>レジストリ (ConsentStore) 側の記録だけを返す。診断表示で内訳を見せるために分けてある。</summary>
    public static HashSet<string> GetFromConsentStore()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ユーザー単位のアプリと、サービスとして動くものの両方を見る。
        CollectFrom(Registry.CurrentUser, result);
        CollectFrom(Registry.LocalMachine, result);

        return result;
    }

    private static void CollectFrom(RegistryKey hive, HashSet<string> result)
    {
        try
        {
            using var store = hive.OpenSubKey(ConsentStorePath);
            if (store is null)
            {
                return;
            }

            foreach (var name in store.GetSubKeyNames())
            {
                using var child = store.OpenSubKey(name);
                if (child is null)
                {
                    continue;
                }

                if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    CollectNonPackaged(child, result);
                }
                else if (IsInUse(child))
                {
                    // ストアアプリはパッケージ名しか分からないので、便宜上 .exe を付けて扱う。
                    result.Add(ToExecutableName(name));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 読めない範囲は諦めてよい。検知できないだけで、手動録音は使える。
        }
    }

    private static void CollectNonPackaged(RegistryKey nonPackaged, HashSet<string> result)
    {
        foreach (var name in nonPackaged.GetSubKeyNames())
        {
            using var entry = nonPackaged.OpenSubKey(name);
            if (entry is null || !IsInUse(entry))
            {
                continue;
            }

            // キー名はフルパスの \ を # に置き換えたもの。例: C:#Program Files#Zoom#bin#Zoom.exe
            var lastSeparator = name.LastIndexOf('#');
            var fileName = lastSeparator >= 0 ? name[(lastSeparator + 1)..] : name;
            result.Add(fileName);
        }
    }

    private static bool IsInUse(RegistryKey key)
    {
        // 使用中は Stop が 0 のまま。Start だけ見ても過去の利用と区別できない。
        if (key.GetValue("LastUsedTimeStop") is not long stop)
        {
            return false;
        }

        return stop == 0;
    }

    private static string ToExecutableName(string packageFamilyName)
    {
        var underscore = packageFamilyName.IndexOf('_');
        var baseName = underscore > 0 ? packageFamilyName[..underscore] : packageFamilyName;
        return baseName + ".exe";
    }
}
