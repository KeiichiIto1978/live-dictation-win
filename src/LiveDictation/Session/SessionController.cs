using LiveDictation.Config;
using LiveDictation.Diagnostics;
using LiveDictation.Meetings;
using Timer = System.Threading.Timer;

namespace LiveDictation.Session;

public enum RecorderStatus
{
    /// <summary>待機中。会議を検知したら自動で録り始める。</summary>
    Idle,

    /// <summary>録音中。</summary>
    Recording,
}

/// <summary>
/// アプリ全体の司令塔。会議検知・録音・停止後の文字起こしをつなぐ。
/// UI からは、このクラスだけを触れば済むようにしている。
/// </summary>
public sealed class SessionController : IDisposable
{
    private readonly object _gate = new();
    private readonly MeetingDetector _detector;
    private readonly Timer _watchdog;

    private RecordingSession? _session;
    private bool _startedByMeeting;
    private bool _disposed;

    public event Action? StateChanged;

    public AppSettings Settings { get; }

    public RecorderStatus Status { get; private set; } = RecorderStatus.Idle;

    public string StatusDetail { get; private set; } = "待機中";

    public RecordingSession? CurrentSession => _session;

    /// <summary>停止後の文字起こしが走っているか。</summary>
    public bool FinalPassRunning { get; private set; }

    /// <summary>停止後の文字起こしの進捗表示。</summary>
    public string FinalPassStatus { get; private set; } = string.Empty;

    /// <summary>停止後の文字起こしの進捗 (0.0〜1.0)。長い録音では数分かかるため画面に出す。</summary>
    public double FinalPassProgress { get; private set; }

    public SessionController(AppSettings settings)
    {
        Settings = settings;

        _detector = new MeetingDetector(
            TimeSpan.FromSeconds(settings.MeetingGraceSeconds),
            settings.KeepMeetingAliveWhilePlaying);
        _detector.StateChanged += OnMeetingStateChanged;

        // 止め忘れ対策。手動録音は上限時間で自動的に切る。
        _watchdog = new Timer(_ => CheckMaxDuration(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        RecoverPendingSessions();
    }

    /// <summary>
    /// 音声はあるのに文字起こしが無いセッションを拾い直す。
    ///
    /// アプリ終了やクラッシュで停止後の処理が流れた場合、WAV だけが残る。
    /// 次の起動時にここで回収しないと、利用者は録れているのに結果が無い状態に置かれる。
    /// </summary>
    private void RecoverPendingSessions()
    {
        if (!Settings.EnableFinalPass)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var root = Settings.ResolvedSessionRoot;
                if (!System.IO.Directory.Exists(root))
                {
                    return;
                }

                foreach (var directory in System.IO.Directory.GetDirectories(root).OrderBy(path => path))
                {
                    var audio = System.IO.Path.Combine(directory, "microphone.wav");
                    var transcript = System.IO.Path.Combine(directory, FinalPass.FinalTranscriptFileName);

                    if (!System.IO.File.Exists(audio) || System.IO.File.Exists(transcript))
                    {
                        continue;
                    }

                    if (new System.IO.FileInfo(audio).Length <= 64)
                    {
                        continue;
                    }

                    Log.Info($"未処理のセッションを見つけたため文字起こしします: {directory}");
                    FinalPassRunning = true;
                    FinalPassStatus = "前回の録音を文字起こし中...";
                    StateChanged?.Invoke();

                    FinalPass.Run(
                        Settings,
                        directory,
                        line => FinalPassStatus = line,
                        fraction => FinalPassProgress = fraction);
                }
            }
            catch (Exception ex)
            {
                Log.Error("未処理セッションの回収に失敗しました", ex);
            }
            finally
            {
                FinalPassRunning = false;
                StateChanged?.Invoke();
            }
        });
    }

    /// <summary>文字起こしに使うモデル名。未設定なら null。</summary>
    public string? TranscriptionModelName
    {
        get
        {
            var directory = Settings.OfflineModelDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Settings.ModelSearchRoots
                    .Select(Recognition.OfflineModelLocator.FindDefault)
                    .FirstOrDefault(found => found is not null) ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(directory) ? null : System.IO.Path.GetFileName(directory);
        }
    }

    /// <summary>
    /// 文字起こしできる状態か。
    /// モデルが無くても録音自体はできるため、録音を止めはしない（音は残る）。
    /// </summary>
    public bool CanTranscribe => FinalPass.IsAvailable(Settings);

    private void OnMeetingStateChanged(MeetingState state)
    {
        if (!Settings.AutoRecordEnabled)
        {
            return;
        }

        if (state.IsMeeting)
        {
            Start($"{state.AppName} を検知", startedByMeeting: true);
            return;
        }

        // 手動で始めた録音は、会議アプリの都合で勝手に止めない。
        lock (_gate)
        {
            if (!_startedByMeeting)
            {
                return;
            }
        }

        Stop();
    }

    private void CheckMaxDuration()
    {
        RecordingSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            return;
        }

        if (session.Elapsed >= TimeSpan.FromHours(Settings.MaxRecordingHours))
        {
            Log.Warn($"上限時間 {Settings.MaxRecordingHours} 時間に達したため録音を止めます。");
            Stop();
        }
    }

    public void Start(string? trigger, bool startedByMeeting = false)
    {
        lock (_gate)
        {
            if (_disposed || _session is not null)
            {
                return;
            }

            try
            {
                _session = new RecordingSession(Settings, trigger);
                _startedByMeeting = startedByMeeting;
                Status = RecorderStatus.Recording;
                StatusDetail = trigger ?? "録音中";
            }
            catch (Exception ex)
            {
                Log.Error("録音を開始できませんでした", ex);
                StatusDetail = "録音開始に失敗";
            }
        }

        StateChanged?.Invoke();
    }

    public void Stop()
    {
        RecordingSession? session;

        lock (_gate)
        {
            session = _session;
            _session = null;
            _startedByMeeting = false;

            if (session is not null)
            {
                Status = RecorderStatus.Idle;
                StatusDetail = "待機中";
            }
        }

        if (session is null)
        {
            return;
        }

        var directory = session.Directory;
        session.Dispose();

        StartFinalPass(directory);

        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        if (Status == RecorderStatus.Recording)
        {
            Stop();
        }
        else
        {
            Start("手動");
        }
    }

    /// <summary>
    /// 録音停止後に文字起こしを作る。UI を止めないよう裏で走らせる。
    /// </summary>
    private void StartFinalPass(string sessionDirectory)
    {
        if (!Settings.EnableFinalPass)
        {
            return;
        }

        FinalPassRunning = true;
        FinalPassStatus = "文字起こしを準備中...";
        FinalPassProgress = 0;
        StateChanged?.Invoke();

        Task.Run(() =>
        {
            try
            {
                var ok = FinalPass.Run(
                    Settings,
                    sessionDirectory,
                    line => FinalPassStatus = line,
                    fraction => FinalPassProgress = fraction);
                FinalPassStatus = ok ? "文字起こし完了" : "文字起こしに失敗";
            }
            catch (Exception ex)
            {
                Log.Error("文字起こしで例外が発生しました", ex);
                FinalPassStatus = "文字起こしに失敗";
            }
            finally
            {
                FinalPassRunning = false;
                StateChanged?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _detector.StateChanged -= OnMeetingStateChanged;
        _detector.Dispose();
        _watchdog.Dispose();

        // 終了時に停止後の処理を始めても、プロセスが落ちて道連れになる。
        // ここでは録音を閉じるだけにして、文字起こしは次回起動時に回収する。
        RecordingSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            session.Dispose();
            Log.Info("終了のため録音を閉じました。文字起こしは次回起動時に行います。");
        }
    }
}
