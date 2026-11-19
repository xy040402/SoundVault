using System.IO;
using System.Text;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoundVault.App.Audio;

namespace SoundVault.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ── 单实例：第二个实例只负责把已有的窗口抬到前台（SV_ALLOW_MULTI=1 可关掉，便于测试）──
        if (Environment.GetEnvironmentVariable("SV_ALLOW_MULTI") != "1")
        {
            _instanceMutex = new Mutex(true, @"Local\SoundVault.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                // 右键「添加到音匣」：把路径转交给已经开着的窗口，再自己退出
                var pending2 = ArgAfter(e.Args, "--add");
                if (pending2 is not null) SoundVault.App.Services.FileAssociation.SendToRunningInstance(pending2);

                ActivateRunningInstance();
                Shutdown();
                return;
            }

            // 首个实例：把 --add 记下来，等主窗口建好后自己处理
            SoundVault.App.MainWindow.PendingAdd = ArgAfter(e.Args, "--add");
        }

        base.OnStartup(e);

        // 崩溃日志：任何未处理异常都写到 %TEMP%\sv-crash.txt，便于现场定位
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "sv-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {args.Exception}\n\n",
                    Encoding.UTF8);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "sv-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] FATAL {args.ExceptionObject}\n\n",
                    Encoding.UTF8);
            }
            catch { }
        };

        // 离线自检：--selftest <音频文件> [输出文件]
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            string result;
            try { result = AudioEngine.RunSelfTest(e.Args[1]); }
            catch (Exception ex) { result = "SELFTEST EXCEPTION: " + ex; }

            if (e.Args.Length >= 3)
                File.WriteAllText(e.Args[2], result, Encoding.UTF8);

            Shutdown(0);
            return;
        }

        // 实机播放自检：--playtest <音频文件> <毫秒> [输出文件]
        if (e.Args.Length >= 3 && e.Args[0] == "--playtest")
        {
            var text = AudioEngine.RunPlayTest(e.Args[1], int.TryParse(e.Args[2], out var ms) ? ms : 1500);
            if (e.Args.Length >= 4) File.WriteAllText(e.Args[3], text, Encoding.UTF8);
            Shutdown(0);
            return;
        }

        new MainWindow().Show();
    }

    private static string? ArgAfter(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    /// <summary>把已经开着的那个音匣窗口抬到前台（最小化了就还原）。</summary>
    private static void ActivateRunningInstance()
    {
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                if (proc.ProcessName is not ("音匣" or "SoundVault.App")) continue;

                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
                return;
            }
            catch { }
        }
    }
}
