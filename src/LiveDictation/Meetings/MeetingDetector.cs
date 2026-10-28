using LiveDictation.Diagnostics;
using Timer = System.Threading.Timer;

namespace LiveDictation.Meetings;

public sealed record MeetingState(bool IsMeeting, MeetingApp? App)
{
    public static readonly MeetingState Idle = new(false, null);

    public string? AppName => App?.DisplayName;
}

/// <summary>
/// 会議アプリの状態を監視する。録音そのものは行わず、状態の変化だけを通知する。
///
/// 判定は「開始」と「継続」で条件を変えている。
/// マイクの解放は、ミュートと退出のどちらでも同じように観測されるため、
/// マイクだけを見ていると「ミュートして聞き役に回った」だけで会議終了と誤判定する。
/// 会議中は相手の声が鳴り続けるので、継続の判定には再生も含める。
/// </summary>
public sealed class MeetingDetector : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _grace;
    private readonly bool _keepAliveWhilePlaying;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private MeetingState _current = MeetingState.Idle;
    private DateTime _lastSeenUtc = DateTime.MinValue;
    private bool _disposed;

    public event Action<MeetingState>? StateChanged;

    public MeetingState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public MeetingDetector(TimeSpan grace, bool keepAliveWhilePlaying)
    {
        _grace = grace;
        _keepAliveWhilePlaying = keepAliveWhilePlaying;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    private void Poll()
    {
        try
        {
            MeetingApp? ongoing;
            bool isMeeting;

            lock (_gate)
            {
                ongoing = _current.App;
                isMeeting = _current.IsMeeting;
            }

            // 会議中かどうかで見る条件を変えるため、先に現在の状態を取り出している。
            // 会議が終わったあとに継続条件を使うと、同じアプリで動画を再生しただけで
            // 会議が復活してしまうため、開始の判定は必ず厳しいほうを通す。
            var detected = isMeeting && ongoing is not null && IsOngoing(ongoing)
                ? ongoing
                : DetectStart();


            MeetingState? changed = null;
            string? endedApp = null;

            lock (_gate)
            {
                if (detected is not null)
                {
                    _lastSeenUtc = DateTime.UtcNow;

                    if (!_current.IsMeeting)
                    {
                        _current = new MeetingState(true, detected);
                        changed = _current;
                    }
                }
                else if (_current.IsMeeting && DateTime.UtcNow - _lastSeenUtc > _grace)
                {
                    endedApp = _current.AppName;
                    _current = MeetingState.Idle;
                    changed = _current;
                }
            }

            if (changed is not null)
            {
                Log.Info(changed.IsMeeting
                    ? $"会議を検知しました: {changed.AppName}"
                    : $"会議の終了を検知しました: {endedApp}");

                StateChanged?.Invoke(changed);
            }
        }
        catch (Exception ex)
        {
            Log.Error("会議検知の更新に失敗しました", ex);
        }
    }

    /// <summary>
    /// 会議の始まりとみなす条件。誤検知を避けるため厳しくする。
    ///
    /// マイクを掴んでいるだけでは会議と断定できない。会議以外でもマイクは使われる
    /// （ブラウザの音声入力や録音サイト、各アプリのマイクテスト画面）。
    /// 同じアプリが音も出していることまで確かめる。
    /// </summary>
    private static MeetingApp? DetectStart()
    {
        var microphoneUsers = MicrophoneUsageReader.GetExecutablesInUse();
        if (microphoneUsers.Count == 0)
        {
            return null;
        }

        // 会議アプリが 1 つも無ければ再生側を読む必要がないため、必要になってから取る。
        HashSet<string>? playing = null;

        foreach (var executable in microphoneUsers)
        {
            var app = MeetingApps.Match(executable);
            if (app is null)
            {
                continue;
            }

            playing ??= AudioSessionReader.GetPlaying();
            if (playing.Contains(executable))
            {
                return app;
            }
        }

        return null;
    }

    /// <summary>
    /// 会議が続いているとみなす条件。
    /// マイクを手放していても、相手の声が鳴っていれば会議中とみなす。
    /// </summary>
    private bool IsOngoing(MeetingApp app)
    {
        var microphoneUsers = MicrophoneUsageReader.GetExecutablesInUse();
        foreach (var executable in app.Executables)
        {
            if (microphoneUsers.Contains(executable))
            {
                return true;
            }
        }

        if (!_keepAliveWhilePlaying)
        {
            return false;
        }

        var playing = AudioSessionReader.GetPlaying();
        foreach (var executable in app.Executables)
        {
            if (playing.Contains(executable))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}
