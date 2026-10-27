using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiveDictation.Recognition;
using LiveDictation.Session;
using Application = System.Windows.Application;

namespace LiveDictation.Tray;

/// <summary>
/// タスクトレイ常駐。mac 版のメニューバー常駐に対応する部分。
/// ウィンドウを閉じてもここが生き残り、会議検知を続ける。
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly SessionController _controller;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly Action _showWindow;

    private Icon? _currentIcon;
    private bool _disposed;

    public TrayPresence(SessionController controller, Action showWindow)
    {
        _controller = controller;
        _showWindow = showWindow;

        _statusItem = new ToolStripMenuItem("待機中") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("録音を開始", null, (_, _) => _controller.Toggle());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("ウィンドウを表示", null, (_, _) => _showWindow()));
        menu.Items.Add(new ToolStripMenuItem("保存フォルダを開く", null, (_, _) => OpenSessionFolder()));
        menu.Items.Add(new ToolStripMenuItem("置換辞書を開く", null, (_, _) => OpenReplacements()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => Application.Current.Shutdown()));

        _notifyIcon = new NotifyIcon
        {
            Text = "文字起こしちゃん for Windows",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _showWindow();

        _controller.StateChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        // 会議検知タイマーなど UI 以外のスレッドから呼ばれるため、必ず UI スレッドに寄せる。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Refresh);
            return;
        }

        var recording = _controller.Status == RecorderStatus.Recording;

        // 文字起こしモデルが無くても録音はできるので、録音不可ではなく注意として見せる。
        var next = recording
            ? TrayIconFactory.CreateRecording()
            : _controller.TranscriptionModelName is null
                ? TrayIconFactory.CreateUnavailable()
                : TrayIconFactory.CreateIdle();

        _notifyIcon.Icon = next;
        _currentIcon?.Dispose();
        _currentIcon = next;

        _statusItem.Text = _controller.FinalPassRunning
            ? _controller.FinalPassStatus
            : _controller.StatusDetail;
        _toggleItem.Text = recording ? "録音を停止" : "録音を開始";

        // Text は 63 文字までしか入らないため、短く組み立てる。
        _notifyIcon.Text = recording
            ? "文字起こし中"
            : _controller.FinalPassRunning ? "文字起こしを仕上げ中" : "文字起こしちゃん（待機中）";
    }

    private static void OpenReplacements()
    {
        var path = ReplacementsTemplate.EnsureExists();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenSessionFolder()
    {
        var path = _controller.CurrentSession?.Directory ?? _controller.Settings.ResolvedSessionRoot;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= Refresh;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
