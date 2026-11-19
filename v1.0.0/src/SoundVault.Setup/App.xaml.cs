using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace SoundVault.Setup;

public partial class App : Application
{
    /// <summary>让正在运行的 exe 在退出后自己删掉（借 cmd 延迟一拍）。</summary>
    private static void SelfDelete()
    {
        try
        {
            var self = Environment.ProcessPath;
            if (self is null) return;
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool Has(string flag) => e.Args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // ① 提权重开安装：音匣安装包.exe --elevated <目录> → 带界面安装（有进度条、有完成提示）
        int elev = Array.FindIndex(e.Args, a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
        if (elev >= 0 && elev + 1 < e.Args.Length)
        {
            var w = new SetupWindow();
            w.PresetTarget(Installer.ResolveInstallDir(e.Args[elev + 1]), autoStart: true);
            MainWindow = w;
            w.Show();
            return;
        }

        // ② 静默安装：--silent <目录>（验收 / 批量部署用）
        int silent = Array.FindIndex(e.Args, a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silent >= 0 && silent + 1 < e.Args.Length)
        {
            try
            {
                Installer.Install(Installer.ResolveInstallDir(e.Args[silent + 1]),
                    createStartMenu: true, createDesktop: true, progress: (_, _) => { });
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-install.txt"),
                    "OK dir=" + e.Args[silent + 1] + Environment.NewLine);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-install.txt"), "FAILED: " + ex + Environment.NewLine);
            }
            Shutdown();
            return;
        }

        // ③ 收尾：--cleanup <目录> [发起卸载的 PID]
        int ci = Array.FindIndex(e.Args, a => a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase));
        if (ci >= 0 && ci + 1 < e.Args.Length)
        {
            var dir = e.Args[ci + 1];

            // 等发起卸载的进程真正退出：它的 exe 还占着文件，早删会失败
            if (ci + 2 < e.Args.Length && int.TryParse(e.Args[ci + 2], out var waitPid))
            {
                try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(15000); } catch { }
            }

            for (int attempt = 0; attempt < 15 && Directory.Exists(dir); attempt++)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                    }
                    Directory.Delete(dir, recursive: true);
                }
                catch { }
                if (Directory.Exists(dir)) Thread.Sleep(400);
            }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-uninstall.txt"),
                    "CLEANED leftover=" + Directory.Exists(dir) + Environment.NewLine);
            }
            catch { }

            SelfDelete();
            Shutdown();
            return;
        }

        // ④ 卸载：双击「卸载 音匣.exe」/ 带 --uninstall 都算；--yes 走静默
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "");
        var isUninstaller = exeName.StartsWith("卸载", StringComparison.OrdinalIgnoreCase) || Has("--uninstall");
        if (isUninstaller)
        {
            var baseDir = (AppContext.BaseDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar);

            // 装在 Program Files 这种受保护目录：卸载程序自己也得提权才删得动文件
            if (!Has("--elevated-uninstall") && baseDir.Length > 0 && !Installer.CanWriteTo(baseDir))
            {
                try
                {
                    var self = Environment.ProcessPath ?? "";
                    var psi = new System.Diagnostics.ProcessStartInfo(self, "--elevated-uninstall")
                    { UseShellExecute = true, Verb = "runas" };
                    System.Diagnostics.Process.Start(psi);
                }
                catch { /* 用户拒绝提权：退回普通模式 */ }
                Shutdown();
                return;
            }

            if (Has("--yes"))
            {
                Uninstaller.Run(skipConfirm: true);
                Shutdown();
                return;
            }

            var uw = new UninstallWindow();
            MainWindow = uw;
            uw.Show();
            return;
        }

        // ⑤ 默认：显示安装界面
        var win = new SetupWindow();
        MainWindow = win;
        win.Show();
    }
}
