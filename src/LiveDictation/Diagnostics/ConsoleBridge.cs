using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveDictation.Diagnostics;

/// <summary>
/// GUI アプリ (WinExe) は標準出力が繋がっていないため、診断モードの結果が端末に出ない。
/// 起動元のコンソールに接続し直して、コマンドラインから実行したときだけ結果が見えるようにする。
/// </summary>
public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static bool Attach()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            return false;
        }

        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.OutputEncoding = Encoding.UTF8;
            return true;
        }
        catch (IOException)
        {
            // コンソールに書けない環境ではファイル出力だけで続行する。
            return false;
        }
    }
}
