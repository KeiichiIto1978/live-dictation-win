using System.Runtime.InteropServices;

namespace LiveDictation.Tray;

internal static class NativeMethods
{
    /// <summary>Bitmap.GetHicon で得たハンドルは明示的に解放しないとリークする。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
}
