using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LiveDictation.Session;

namespace LiveDictation;

public partial class MainWindow : Window
{
    private readonly SessionController _controller;
    private readonly DispatcherTimer _meterTimer;

    private string? _lastTranscriptPath;

    public MainWindow(SessionController controller)
    {
        InitializeComponent();

        _controller = controller;
        AutoRecordCheck.IsChecked = controller.Settings.AutoRecordEnabled;

        _controller.StateChanged += OnStateChanged;

        // レベル表示はイベントではなく定期更新にする。
        // 音声スレッドから 100ms ごとに飛んでくるものをそのまま描くと、UI が追われるため。
        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        Closed += (_, _) =>
        {
            _controller.StateChanged -= OnStateChanged;
            _meterTimer.Stop();
        };

        UpdateState();
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(UpdateState);

    private void UpdateMeters()
    {
        UpdateFinalPassProgress();

        var session = _controller.CurrentSession;

        if (session is null)
        {
            MicrophoneMeter.Value = 0;
            SystemMeter.Value = 0;
            MicrophoneState.Text = string.Empty;
            SystemState.Text = string.Empty;
            return;
        }

        MicrophoneMeter.Value = session.MicrophoneLevel * 100;
        SystemMeter.Value = session.SystemPlaybackLevel * 100;

        // 受け取った秒数が増えていれば、無音でも取り込みは生きている。
        MicrophoneState.Text = session.IsMicrophoneActive
            ? $"{session.MicrophoneSeconds:0.0} 秒受信"
            : "取得できず";

        SystemState.Text = session.IsSystemPlaybackActive
            ? $"{session.SystemPlaybackSeconds:0.0} 秒受信"
            : "取得できず";
    }

    /// <summary>
    /// 文字起こしの進捗を定期的に描き直す。
    /// 状態変化のイベントだけでは開始と終了しか拾えず、
    /// 1 時間の録音では数分間なにも動かないように見えるため。
    /// </summary>
    private void UpdateFinalPassProgress()
    {
        if (!_controller.FinalPassRunning)
        {
            if (FinalPassPanel.Visibility != Visibility.Collapsed)
            {
                FinalPassPanel.Visibility = Visibility.Collapsed;
            }

            return;
        }

        FinalPassPanel.Visibility = Visibility.Visible;
        FinalPassBar.Value = Math.Clamp(_controller.FinalPassProgress, 0, 1) * 100;
        FinalPassText.Text = $"{_controller.FinalPassStatus}（{_controller.FinalPassProgress * 100:0}%）";
    }

    private void UpdateState()
    {
        var recording = _controller.Status == RecorderStatus.Recording;
        var finalizing = _controller.FinalPassRunning;

        StatusText.Text = recording
            ? "録音中"
            : finalizing ? "文字起こし中" : "待機中";

        DetailText.Text = recording
            ? BuildRecordingDetail()
            : finalizing
                ? _controller.FinalPassStatus
                : "会議アプリを検知すると自動で録音を始めます。";

        ToggleButton.Content = recording ? "録音を停止" : "録音を開始";

        if (_controller.TranscriptionModelName is null)
        {
            ModelText.Text = "文字起こしモデル: 未設定（録音のみ）";
            ModelText.ToolTip = "scripts フォルダの download-offline-model.ps1 を実行するとモデルが入ります。";
            TranscriptGuide.Text =
                "文字起こしモデルが見つかりません。録音は WAV に残るので、モデルを入れたあとに "
                + "--finalize をかければ文字起こしできます。";
        }
        else
        {
            ModelText.Text = "文字起こしモデル: " + _controller.TranscriptionModelName;
            ModelText.ToolTip = null;
            TranscriptGuide.Text =
                "録音を停止すると、日本語モデルで文字起こしして transcript.md に書き出します。";
        }

        UpdateLastTranscript();
    }

    private void UpdateLastTranscript()
    {
        // 直近に出来上がったものへ、すぐ飛べるようにしておく。
        var path = FindLatestTranscript();

        _lastTranscriptPath = path;
        OpenTranscriptButton.IsEnabled = path is not null;

        LastTranscriptText.Text = path is null ? string.Empty : "最新: " + path;
    }

    private string? FindLatestTranscript()
    {
        try
        {
            var root = _controller.Settings.ResolvedSessionRoot;
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetDirectories(root)
                .Select(directory => Path.Combine(directory, FinalPass.FinalTranscriptFileName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string BuildRecordingDetail()
    {
        var session = _controller.CurrentSession;
        return session is null ? _controller.StatusDetail : $"{_controller.StatusDetail} — {session.Directory}";
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) => _controller.Toggle();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _controller.CurrentSession?.Directory ?? _controller.Settings.ResolvedSessionRoot;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnOpenTranscriptClick(object sender, RoutedEventArgs e)
    {
        if (_lastTranscriptPath is null || !File.Exists(_lastTranscriptPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_lastTranscriptPath) { UseShellExecute = true });
    }

    private void OnAutoRecordClick(object sender, RoutedEventArgs e)
    {
        _controller.Settings.AutoRecordEnabled = AutoRecordCheck.IsChecked == true;
        _controller.Settings.Save();
    }
}
