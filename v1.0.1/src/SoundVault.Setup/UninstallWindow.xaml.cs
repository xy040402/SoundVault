using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace SoundVault.Setup;

/// <summary>
/// 卸载向导（3 步）：确认 → 卸载中 → 完成。和安装器同一套外观。
/// </summary>
public partial class UninstallWindow : Window
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundVault";

    private readonly string _dir;
    private int _step;                 // 0 确认 / 1 卸载中 / 2 完成
    private bool _deleteData;
    private bool _running;

    public UninstallWindow()
    {
        InitializeComponent();

        SideVersion.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        _dir = (AppContext.BaseDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar);

        DirText.Text = _dir.Length > 0 ? _dir : "（未知）";
        DataText.Text = DataDir;
        SetStep(0);
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVault");

    // ── 步骤 ───────────────────────────────────────────────

    private void SetStep(int s)
    {
        _step = s;

        PageConfirm.Visibility = s == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageRunning.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageDone.Visibility = s == 2 ? Visibility.Visible : Visibility.Collapsed;

        ActionBtn.Content = s == 2 ? "完成" : "卸载";
        ActionBtn.Style = (Style)FindResource(s == 2 ? "PrimaryBtn" : "DangerBtn");
        ActionBtn.IsEnabled = s != 1;
        CancelBtn.Content = s == 2 ? "关闭" : "取消";
        CancelBtn.Visibility = s == 2 ? Visibility.Collapsed : Visibility.Visible;
        CancelBtn.IsEnabled = s != 1;
        CloseBtn.IsEnabled = s != 1;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;          // 正在删的时候不让关
        if (_step == 2) FinishAndClose();
        else Close();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        if (_step == 2) { FinishAndClose(); return; }

        _ = RunUninstallAsync();
    }

    // ── 卸载 ───────────────────────────────────────────────

    private async Task RunUninstallAsync()
    {
        _deleteData = DeleteDataBox.IsChecked == true;

        if (_deleteData)
        {
            var sure = MessageBox.Show(this,
                "确定要一并删除软件数据吗？\n\n" +
                "会删掉：评分、收藏、标签、集合、播放历史（" + DataDir + "）\n" +
                "删除后无法恢复；你在硬盘上的音效文件不受影响。",
                "音匣 SoundVault · 请再确认一次", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (sure != MessageBoxResult.Yes)
            {
                DeleteDataBox.IsChecked = false;
                _deleteData = false;
            }
        }

        _running = true;
        SetStep(1);
        SetupLog.Info("卸载界面", $"开始卸载 目录={_dir} 同时删数据={_deleteData}");

        await Task.Yield();           // 先画出"卸载中"这一页

        double trackWidth = ProgressTrack.ActualWidth > 4 ? ProgressTrack.ActualWidth - 2 : 300;
        void Report(double done, string text)
        {
            ProgressFill.Width = Math.Clamp(done, 0, 1) * trackWidth;
            LogText.Text = text;
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(150);
        }

        try
        {
            // 1) 关掉正在运行的音匣
            var procs = Process.GetProcessesByName("音匣").Concat(Process.GetProcessesByName("SoundVault.App")).ToArray();
            if (procs.Length > 0)
            {
                Report(0.1, $"正在关闭音匣（{procs.Length} 个进程）…");
                foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
                Thread.Sleep(700);
                foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch { } }
                Thread.Sleep(400);
            }

            // 2) 快捷方式
            Report(0.3, "正在移除开始菜单与桌面快捷方式…");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "音匣 SoundVault.lnk");
            try { if (File.Exists(desktop)) File.Delete(desktop); } catch { }
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "音匣 SoundVault");
            try { if (Directory.Exists(startMenu)) Directory.Delete(startMenu, recursive: true); } catch { }

            // 3) 注册表（只删指向自己这个目录的登记项）
            Report(0.45, "正在清理「应用和功能」里的登记项…");
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                var loc = key?.GetValue("InstallLocation") as string ?? "";
                if (loc.Length == 0 || string.Equals(loc.TrimEnd(Path.DirectorySeparatorChar), _dir, StringComparison.OrdinalIgnoreCase))
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            }
            catch { }

            // 4) 程序文件
            Report(0.6, "正在删除程序文件…");
            foreach (var name in new[] { "音匣.exe", "SoundVault.App.exe" })
            {
                var f = Path.Combine(_dir, name);
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }

            // 5) 软件数据（可选）
            if (_deleteData)
            {
                Report(0.85, "正在删除软件数据（评分 / 标签 / 集合）…");
                try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true); } catch { }
            }
            else
            {
                Report(0.85, "软件数据已保留。");
            }

            SetupLog.Info("卸载界面", "卸载完成 ✔");
            Report(1.0, "卸载完成 ✔");

            DoneText.Text = _deleteData
                ? "程序已移除，软件数据也已删除。"
                : "程序已移除，软件数据保留着 —— 以后重装音匣会接着用，评分和标签都还在。";
            DoneDataText.Text = _deleteData ? "" : "数据位置：" + DataDir;

            _running = false;
            SetStep(2);
        }
        catch (Exception ex)
        {
            SetupLog.Error("卸载界面", "卸载出错", ex);
            _running = false;
            SetStep(0);
            Headline.Text = "卸载没有完成";
            IntroText.Text = "出错了：" + ex.Message + "\n可以关掉这个窗口，手动删除安装目录。";
        }
    }

    /// <summary>点「完成/关闭」后，把自己复制到临时目录去删掉整个安装目录（含本卸载程序）。</summary>
    private void FinishAndClose()
    {
        try
        {
            var self = Environment.ProcessPath ?? "";
            if (File.Exists(self) && Directory.Exists(_dir) && self.StartsWith(_dir, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "sv-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, tmp, overwrite: true);
                Process.Start(new ProcessStartInfo(tmp, "--cleanup \"" + _dir + "\" " + Environment.ProcessId) { UseShellExecute = true });
            }
        }
        catch { }
        Close();
    }
}
