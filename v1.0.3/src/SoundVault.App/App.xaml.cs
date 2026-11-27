using System.IO;
using System.Text;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoundVault.App.Audio;
using SoundVault.App.Services;

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

        // ── 日志 ───────────────────────────────────────────────
        Log.Start();

        // 崩溃：先写正式日志（带堆栈），再留一份 %TEMP%\sv-crash.txt 兜底
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("崩溃", "界面线程未处理异常", args.Exception);
            WriteCrashFallback("DISPATCHER", args.Exception);
            Log.Flush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Error("崩溃", "后台线程未处理异常（致命）", ex ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
            WriteCrashFallback("APPDOMAIN", ex);
            Log.Flush();
        };

        Log.Info("启动", $"{typeof(App).Assembly.GetName().Version} 参数=[{string.Join(' ', e.Args)}]");
        Log.StartWatchdog();
        foreach (var line in Log.SystemInfo().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log.Info("环境", line.TrimEnd('\r').TrimEnd());

        // 测试钩子：SV_CRASH_TEST=1 时故意抛异常，用来验证崩溃日志
        if (Environment.GetEnvironmentVariable("SV_CRASH_TEST") == "1")
        {
            Log.Info("自检", "SV_CRASH_TEST=1，即将抛出测试异常");
            throw new InvalidOperationException("日志自检：故意抛出的异常（SV_CRASH_TEST）");
        }

        // WAV 头自检：--wavinfo <音频> [输出文件]
        if (e.Args.Length >= 2 && e.Args[0] == "--wavinfo")
        {
            var info = SoundVault.App.Audio.WavReader.TryRead(e.Args[1]);
            var text = info is null
                ? "WAVINFO null"
                : string.Join(Environment.NewLine, new[]
                  {
                      "formatTag = 0x" + info.FormatTag.ToString("X4"),
                      "channels = " + info.Channels,
                      "sampleRate = " + info.SampleRate,
                      "bits = " + info.Bits,
                      "channelMask = 0x" + info.ChannelMask.ToString("X8"),
                      "dataOffset = " + info.DataOffset,
                      "dataLength = " + info.DataLength,
                      "durationMs = " + info.DurationMs.ToString("0.0")
                  });
            if (e.Args.Length >= 3) File.WriteAllText(e.Args[2], text, Encoding.UTF8);
            Log.Stop();
            Shutdown(0);
            return;
        }

        // 降混自检：--downmixstat <音频> [输出文件]
        if (e.Args.Length >= 2 && e.Args[0] == "--downmixstat")
        {
            var text = AudioClip.RunDownmixStat(e.Args[1]);
            if (e.Args.Length >= 3) File.WriteAllText(e.Args[2], text, Encoding.UTF8);
            Log.Info("自检", "--downmixstat 完成");
            Log.Stop();
            Shutdown(0);
            return;
        }

        // 命令行导出日志：--exportlog <zip 路径> [分钟|all] [warn]
        if (e.Args.Length >= 2 && e.Args[0] == "--exportlog")
        {
            try
            {
                var opt = new Log.ExportOptions
                {
                    Window = e.Args.Length >= 3 && e.Args[2] != "all"
                        ? (int.TryParse(e.Args[2], out var mins) ? TimeSpan.FromMinutes(mins) : (TimeSpan?)null)
                        : null,
                    WarningsOnly = e.Args.Contains("warn")
                };
                var r = Log.ExportWindowed(e.Args[1], opt);
                Log.Info("导出", $"命令行导出日志 lines={r.Lines} fails={r.Failures} bytes={r.Bytes}");
                Log.Stop();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Log.Stop();
                Shutdown(1);
            }
            return;
        }

        // 命令行导出诊断包：--diag <zip 路径>（界面点不动时的兜底）
        if (e.Args.Length >= 2 && e.Args[0] == "--diag")
        {
            try
            {
                var path = Log.ExportDiagnostics(e.Args[1]);
                Log.Info("诊断", "命令行导出成功 → " + path);
                Log.Stop();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Log.Error("诊断", "命令行导出失败", ex);
                Console.Error.WriteLine(ex);
                Log.Stop();
                Shutdown(1);
            }
            return;
        }

        // 离线自检：--selftest <音频文件> [输出文件]
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            string result;
            try { result = AudioEngine.RunSelfTest(e.Args[1]); }
            catch (Exception ex) { result = "SELFTEST EXCEPTION: " + ex; }

            if (e.Args.Length >= 3)
                File.WriteAllText(e.Args[2], result, Encoding.UTF8);

            Log.Info("自检", "--selftest 完成");
            Log.Stop();
            Shutdown(0);
            return;
        }

        // 实机播放自检：--playtest <音频文件> <毫秒> [输出文件]
        if (e.Args.Length >= 3 && e.Args[0] == "--playtest")
        {
            var text = AudioEngine.RunPlayTest(e.Args[1], int.TryParse(e.Args[2], out var ms) ? ms : 1500);
            if (e.Args.Length >= 4) File.WriteAllText(e.Args[3], text, Encoding.UTF8);
            Log.Info("自检", "--playtest 完成");
            Log.Stop();
            Shutdown(0);
            return;
        }

        var win = new MainWindow();
        bool hidden = e.Args.Contains("--hidden");

        if (hidden)
        {
            // 开机自启：只进托盘，不闪窗口
            win.WindowState = WindowState.Minimized;
            win.ShowInTaskbar = false;
            win.Show();
            win.Hide();
            Log.Info("启动", "--hidden：只留托盘");
        }
        else
        {
            win.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.StopWatchdog();
        Log.Info("退出", $"正常退出，代码 {e.ApplicationExitCode}");
        Log.Stop();
        base.OnExit(e);
    }

    private static void WriteCrashFallback(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "sv-crash.txt"),
                $"[{DateTime.Now:HH:mm:ss}] {source} {ex}\n\n",
                Encoding.UTF8);
        }
        catch { }
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
