using System.Diagnostics;
using LiveDictation.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LiveDictation.Meetings;

/// <summary>
/// どのアプリが今スピーカーに音を出しているか / マイクを掴んでいるかを、
/// オーディオエンジンのセッションから直接読む。
///
/// マイク側はレジストリ (ConsentStore) でも分かるが、記録が更新されない環境があるため、
/// 実時間の信号であるこちらを併用する。
/// </summary>
public static class AudioSessionReader
{
    /// <summary>スピーカーに音を出しているアプリ。</summary>
    public static HashSet<string> GetPlaying() => Collect(DataFlow.Render);

    /// <summary>マイクを掴んでいるアプリ。</summary>
    public static HashSet<string> GetCapturing() => Collect(DataFlow.Capture);

    private static HashSet<string> Collect(DataFlow flow)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // 既定デバイスだけを見ると取りこぼす。
            // ブラウザの通話音声は「通信用」の既定デバイスへ回されることがあり、
            // 「既定のデバイス」と別物になっている環境では検出できないため。
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    CollectFromDevice(device, result);
                }
            }
        }
        catch (Exception ex)
        {
            // ここが静かに失敗すると会議検知だけが永久に動かない。原因が追えるよう残す。
            Log.Warn($"{flow} のセッションを読めませんでした: {ex.Message}");
        }

        return result;
    }

    private static void CollectFromDevice(MMDevice device, HashSet<string> result)
    {
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();

            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive)
                {
                    continue;
                }

                var name = TryGetProcessName(session.GetProcessID);
                if (name is not null)
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            // デバイスが差し替わった直後などに個別に失敗する。他のデバイスの列挙は続ける。
            Log.Debug($"{device.FriendlyName} のセッションを読めませんでした: {ex.Message}");
        }
    }

    private static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 読み取る前に終了したプロセス。無視してよい。
            return null;
        }
    }
}
