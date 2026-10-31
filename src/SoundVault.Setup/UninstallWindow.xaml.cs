using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace SoundVault.Setup;

/// <summary>
/// 卸载向导：确认 → 选项（是否删数据）→ 进度 → 完成。和安装器同一套外观。
/// </summary>
public partial class UninstallWindow : Window
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundVault";

    private readonly string _dir;
    private bool _running;          // 正在卸载
    private bool _done;             // 卸完，等用户点「完成」

    public UninstallWindow()
    {
        InitializeComponent();
        _dir = (AppContext.BaseDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar);

        DirText.Text = _dir.Length > 0 ? _dir : "（未知）";
        DataText.Text = DataDir + Environment.NewLine + "（评分、标签、集合、播放历史等都在这里）";
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVault");

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;           // 正在删的时候不让关
        if (_done) FinishAndClose();
        else Close();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        // 完成态：这一下是「完成」
        if (_done)
        {
            FinishAndClose();
            return;
        }

        _running = true;
        CancelBtn.IsEnabled = false;
        ActionBtn.IsEnabled = false;
        DeleteDataBox.IsEnabled = false;

        var deleteData = DeleteDataBox.IsChecked == true;
        if (deleteData)
        {
            var sure = MessageBox.Show(this,
                "确定要一并删除软件数据吗？\n\n" +
                "会删掉：评分、收藏、标签、集合、播放历史（" + DataDir + "）\n" +
                "删除后无法恢复；你在硬盘上的音效文件不受影响。",
                "音匣 SoundVault · 请再确认一次", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (sure != MessageBoxResult.Yes)
            {
                DeleteDataBox.IsChecked = false;
                deleteData = false;
            }
        }

        double trackWidth = ProgressTrack.ActualWidth > 4 ? ProgressTrack.ActualWidth - 2 : 300;
        void Report(double done, string text)
        {
            ProgressFill.Width = Math.Clamp(done, 0, 1) * trackWidth;
            LogText.Text = text;
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(160);
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

            // 3) 注册表
            Report(0.45, "正在清理「应用和功能」里的登记项…");
            // 只删指向自己这个目录的登记项（v0.54）
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
            var self = Environment.ProcessPath ?? "";
            foreach (var name in new[] { "音匣.exe", "SoundVault.App.exe" })
            {
                var f = Path.Combine(_dir, name);
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }

            // 5) 软件数据（可选）
            if (deleteData)
            {
                Report(0.8, "正在删除软件数据（评分 / 标签 / 集合）…");
                try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true); } catch { }
            }
            else
            {
                Report(0.8, "软件数据已保留。");
            }

            Report(1.0, "卸载完成 ✔");

            // 完成态
            Headline.Text = "卸载完成";
            IntroText.Text = deleteData
                ? "程序已移除，软件数据也已删除。你的音效文件（硬盘上的音频）从头到尾没有被改动。"
                : "程序已移除，软件数据保留着 —— 以后重装音匣会接着用，评分和标签都还在。";
            LogText.Text = deleteData ? "软件数据已删除。" : "软件数据保留在：" + DataDir;
            DataHint.Visibility = Visibility.Collapsed;
            DeleteDataBox.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;

            ActionBtn.Content = "完成";
            ActionBtn.IsEnabled = true;
            CancelBtn.Content = "关闭";
            CancelBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
            _running = false;        // 关键：不清这个，X 和「完成」都会被上面拦掉
            _done = true;
        }
        catch (Exception ex)
        {
            LogText.Text = "卸载时出错：" + ex.Message;
            _running = false;
            CancelBtn.IsEnabled = true;
            ActionBtn.IsEnabled = true;
            DeleteDataBox.IsEnabled = true;
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
                var tmp = Path.Combine(Path.GetTempPath(), "sv-uninstall-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
                File.Copy(self, tmp, overwrite: true);
                Process.Start(new ProcessStartInfo(tmp, "--cleanup \"" + _dir + "\" " + Environment.ProcessId) { UseShellExecute = true });
            }
        }
        catch { }
        Close();
    }
}
