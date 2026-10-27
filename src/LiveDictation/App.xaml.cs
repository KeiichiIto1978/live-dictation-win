using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LiveDictation.Config;
using LiveDictation.Diagnostics;
using LiveDictation.Session;
using LiveDictation.Tray;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LiveDictation;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private SessionController? _controller;
    private TrayPresence? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Initialize(AppSettings.AppDataRoot);
        Log.Info("起動しました。");

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var settings = AppSettings.Load();

        // 診断モードはコマンドラインから使うため、起動元のコンソールに結果を出す。
        if (e.Args.Length > 0)
        {
            ConsoleBridge.Attach();
        }

        // 会議検知が何を見ているかを実会議中に確認するための経路。
        var watchIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--watch", StringComparison.OrdinalIgnoreCase));
        if (watchIndex >= 0)
        {
            var seconds = 180;
            if (watchIndex + 1 < e.Args.Length && int.TryParse(e.Args[watchIndex + 1], out var parsed) && parsed > 0)
            {
                seconds = parsed;
            }

            var probe = e.Args.Any(arg => string.Equals(arg, "--probe", StringComparison.OrdinalIgnoreCase));
            DetectionWatch.Run(TimeSpan.FromSeconds(seconds), probe);
            Shutdown(0);
            return;
        }

        // 保存済みセッション、または音声ファイル単体を文字起こしする。
        var finalizeIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--finalize", StringComparison.OrdinalIgnoreCase));
        if (finalizeIndex >= 0)
        {
            if (finalizeIndex + 1 >= e.Args.Length)
            {
                Log.Error("--finalize にはセッションフォルダか音声ファイルのパスを指定してください。");
                Shutdown(1);
                return;
            }

            var finalized = Session.FinalPass.Run(settings, e.Args[finalizeIndex + 1], Console.WriteLine);
            Shutdown(finalized ? 0 : 1);
            return;
        }

        // 取り込み経路の確認用に、会議を用意せず同じ処理を通せる録音モード。
        var recordIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--record", StringComparison.OrdinalIgnoreCase));
        if (recordIndex >= 0)
        {
            var recordSeconds = 20;
            if (recordIndex + 1 < e.Args.Length && int.TryParse(e.Args[recordIndex + 1], out var parsedSeconds) && parsedSeconds > 0)
            {
                recordSeconds = parsedSeconds;
            }

            var recorded = HeadlessRecorder.Run(settings, TimeSpan.FromSeconds(recordSeconds));
            Shutdown(recorded ? 0 : 1);
            return;
        }

        // 会議前に単体で確認できるよう、GUI を出さずに診断だけ行う経路を用意する。
        if (e.Args.Any(arg => string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var ok = SelfTest.Run(settings);
            Shutdown(ok ? 0 : 1);
            return;
        }


        // 二重起動を防ぐ。
        // 常駐して会議を自動検知する作りのため、2 つ動くと同じ会議を二重に録音し、
        // マイクの取り合いと CPU の無駄遣いが起きる。
        _instanceLock = new Mutex(initiallyOwned: true, @"Local\LiveDictation.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Log.Warn("すでに起動しているため、この起動は終了します。");
            MessageBox.Show(
                "文字起こしちゃんはすでに起動しています。タスクトレイのアイコンから操作してください。",
                "文字起こしちゃん",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _controller = new SessionController(settings);
        _tray = new TrayPresence(_controller, ShowMainWindow);

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_controller!);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("想定外のエラーが発生しました", e.Exception);

        MessageBox.Show(
            $"想定外のエラーが発生しました。{Environment.NewLine}{e.Exception.Message}",
            "文字起こしちゃん",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // 録音中の内容を失わないよう、画面のエラー程度ではアプリを落とさない。
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("終了します。");
        _tray?.Dispose();
        _controller?.Dispose();

        if (_instanceLock is not null)
        {
            _instanceLock.Dispose();
            _instanceLock = null;
        }

        base.OnExit(e);
    }
}
