using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoundVault.App.Services;

/// <summary>
/// Windows 11 的 DWM 底板（Mica）。Win10 / 关闭了特效的系统上会失败，返回 false，调用方要能优雅退回。
/// 注意：Mica 由 DWM 画在窗口下面，所以窗口自身背景必须是透明的，WPF 的 CompositionTarget 也要设成透明，
/// 否则 WPF 会用不透明黑把我们自己的底板盖掉。
/// </summary>
public static class WindowBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // 老版本是 19
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;       // Win11 22H2+
    private const int DWMSBT_MAINWINDOW = 2;                // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    public static bool IsEnabled { get; private set; }

    /// <summary>打开云母；系统不支持时返回 false 并保持原样。</summary>
    public static bool TryEnable(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // 不要 ExtendFrameIntoClientArea(-1)：那会把窗口画成一圈亮点边框，
            // Win11 22H2 以后只要 38 号属性 + 窗口背景透明就够了
            var margins = new MARGINS();
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 深色标题栏（自绘标题栏用不到，但影响系统画的边框/阴影）
            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            int backdrop = DWMSBT_MAINWINDOW;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (hr != 0) return false;

            // WPF 自己那层必须透明，否则会盖住 DWM 画的云母
            window.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;

            IsEnabled = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关掉云母，恢复成自己画的背景。</summary>
    public static void Disable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                int backdrop = 0;                 // DWMSBT_AUTO
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                var margins = new MARGINS();      // 全 0 = 收回
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                    target.BackgroundColor = Color.FromArgb(0xFF, 0x0A, 0x0C, 0x10);
            }
            catch { }
        }

        window.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        IsEnabled = false;
    }
}
