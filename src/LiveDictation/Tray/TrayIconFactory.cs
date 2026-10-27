using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiveDictation.Tray;

/// <summary>
/// トレイアイコンをその場で描く。
/// 画像ファイルを持たないので、素材のライセンスを気にせず配布できる。
/// </summary>
public static class TrayIconFactory
{
    public static Icon CreateIdle() => Create(Color.FromArgb(120, 130, 145), filled: false);

    public static Icon CreateRecording() => Create(Color.FromArgb(220, 70, 70), filled: true);

    public static Icon CreateUnavailable() => Create(Color.FromArgb(200, 160, 60), filled: false);

    private static Icon Create(Color color, bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var rect = new Rectangle(5, 5, 22, 22);

            if (filled)
            {
                using var brush = new SolidBrush(color);
                graphics.FillEllipse(brush, rect);
            }
            else
            {
                using var pen = new Pen(color, 3f);
                graphics.DrawEllipse(pen, rect);
            }
        }

        // GetHicon のハンドルはアイコン生成後に解放しないとリークする。
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
