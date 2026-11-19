using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace SoundVault.Setup;

public partial class SetupWindow : Window
{
    private const string AppExeName = "音匣.exe";
    private const string UninstallerName = "卸载 音匣.exe";

    private bool _installed;                 // 已经装完，等用户点「完成」
    private string _installedDir = "";

    private bool _autoStart;

    /// <summary>提权重启后用来预填安装位置并自动开装。</summary>
    public void PresetTarget(string dir, bool autoStart)
    {
        PathBox.Text = dir;
        _autoStart = autoStart;
        Loaded += (_, _) =>
        {
            if (!_autoStart) return;
            _autoStart = false;
            Install_Click(InstallBtn, new RoutedEventArgs());
        };
    }

    public SetupWindow()
    {
        InitializeComponent();

        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\SoundVault");

        if (!Installer.HasPayload)
        {
            InstallBtn.IsEnabled = false;
            LogText.Text = "安装包里没有找到主程序（打包时 _local\\dist\\音匣.exe 不存在）。";
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    // ── 升级检测（v0.44）────────────────────────────────────

    /// <summary>音匣是否正在运行（正式版进程名「音匣」，开发版「SoundVault.App」）。</summary>
    private static Process[] RunningApp()
    {
        var list = new List<Process>();
        foreach (var name in new[] { "音匣", "SoundVault.App" })
        {
            try { list.AddRange(Process.GetProcessesByName(name)); } catch { }
        }
        return list.ToArray();
    }

    /// <summary>有正在运行的实例就问一句，同意就关掉。</summary>
    private bool EnsureAppClosed()
    {
        var procs = RunningApp();
        if (procs.Length == 0) return true;

        var answer = MessageBox.Show(this,
            $"音匣正在运行（{procs.Length} 个进程）。\n\n继续安装需要先关掉它，否则文件被占用会失败。\n\n现在关闭音匣并继续吗？",
            "音匣 SoundVault", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return false;

        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { }
        }
        Thread.Sleep(800);
        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
        Thread.Sleep(500);
        return true;
    }

    /// <summary>两个路径是不是同一个目录（忽略大小写与结尾分隔符）。</summary>
    private static bool SameDir(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    /// <summary>从注册表读出已安装的旧版本（路径 + 版本号）。</summary>
    private static bool TryGetExisting(out string path, out string version)
    {
        path = "";
        version = "";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundVault");
            if (key is null) return false;
            path = key.GetValue("InstallLocation") as string ?? "";
            version = key.GetValue("DisplayVersion") as string ?? "";
            return path.Length > 0 && Directory.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>旧版本装在别处：问一句，同意就先把旧版本卸掉。</summary>
    private bool RemoveOldInstall(string oldPath, string oldVersion, string newPath)
    {
        var msg = "检测到已安装的旧版本：\n\n" +
                  $"    位置：{oldPath}\n" +
                  $"    版本：{(oldVersion.Length > 0 ? oldVersion : "未知")}\n\n" +
                  "是否先卸载旧版本，再安装到：\n\n" +
                  $"    位置：{newPath}\n\n" +
                  "（评分、标签、集合等用户数据不会被删除）";
        if (MessageBox.Show(this, msg, "音匣 SoundVault", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return false;

        var uninstaller = Path.Combine(oldPath, UninstallerName);
        if (!File.Exists(uninstaller))
        {
            MessageBox.Show(this,
                $"找不到旧版本的卸载程序：\n{uninstaller}\n\n请手动删除这个文件夹后重试。",
                "音匣 SoundVault", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var psi = new ProcessStartInfo(uninstaller, "--uninstall --yes") { UseShellExecute = true };
        if (!Installer.CanWriteTo(oldPath)) psi.Verb = "runas";      // 装在 Program Files 的要提权
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动旧版本卸载程序失败：" + ex.Message, "音匣 SoundVault",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        LogText.Text = "正在卸载旧版本…";
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        for (int i = 0; i < 60 && Directory.Exists(oldPath); i++)
        {
            Thread.Sleep(400);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        return true;
    }
    /// <summary>用户改完路径离开输入框时，顺手把 SoundVault 补上，免得装到某个目录的根上。</summary>
    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var fixedDir = Installer.ResolveInstallDir(PathBox.Text.Trim());
        if (fixedDir.Length > 0) PathBox.Text = fixedDir;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择安装位置" };
        if (dlg.ShowDialog() == true) PathBox.Text = Installer.ResolveInstallDir(dlg.FolderName);   // 选完就补 SoundVault（v0.43 修）
    }

      private async void Install_Click(object sender, RoutedEventArgs e)
      {
          // 已经装完：这次点的是「完成」→ 启动音匣并关掉安装器
          if (_installed)
          {
              try { Process.Start(new ProcessStartInfo(Path.Combine(_installedDir, AppExeName)) { UseShellExecute = true }); }
              catch { }
              Close();
              return;
          }

          var dir = Installer.ResolveInstallDir(PathBox.Text.Trim());
          PathBox.Text = dir;
          if (dir.Length == 0) return;

          InstallBtn.IsEnabled = false;
          CancelBtn.IsEnabled = false;
          StartMenuBox.IsEnabled = false;
          DesktopBox.IsEnabled = false;

          // 1) 有正在运行的实例？问一句，关掉才继续
          if (!EnsureAppClosed())
          {
              LogText.Text = "已取消：请先关闭正在运行的音匣。";
              ResetButtons();
              return;
          }

          // 2) 旧版本装在别的位置？提示先卸载旧版本
          if (TryGetExisting(out var oldPath, out var oldVersion) && !SameDir(oldPath, dir))
          {
              if (!RemoveOldInstall(oldPath, oldVersion, dir))
              {
                  LogText.Text = "已取消：旧版本未卸载。";
                  ResetButtons();
                  return;
              }
          }

          // 3) 目标目录不可写（比如 Program Files）→ 提权重启自己静默安装
          if (!Installer.CanWriteTo(dir))
          {
              try
              {
                  var exe = Environment.ProcessPath ?? "";
                  var psi = new ProcessStartInfo(exe, $"--elevated \"{dir}\"")
                  {
                      UseShellExecute = true,
                      Verb = "runas"
                  };
                  Process.Start(psi);
                  Application.Current.Shutdown();
                  return;
              }
              catch (Exception ex)
              {
                  LogText.Text = "需要管理员权限才能装到这里，但提权被拒绝：" + ex.Message;
                  ResetButtons();
                  return;
              }
          }

          // 4) 安装（必须在 UI 线程：建快捷方式用 WScript.Shell，COM 需要 STA）
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
              Installer.Install(dir,
                  createStartMenu: StartMenuBox.IsChecked == true,
                  createDesktop: DesktopBox.IsChecked == true,
                  progress: (d, t) => Report(d, t));

              ProgressFill.Width = trackWidth;
              LogText.Text = "安装完成 ✔  点击「完成」启动音匣";
              _installed = true;
              _installedDir = dir;
              InstallBtn.Content = "完成";
              InstallBtn.IsEnabled = true;
              CancelBtn.Content = "关闭";
              CancelBtn.IsEnabled = true;
          }
          catch (Exception ex)
          {
              LogText.Text = "安装失败：" + ex.Message;
              ResetButtons();
          }
      }

      private void ResetButtons()
      {
          InstallBtn.IsEnabled = true;
          CancelBtn.IsEnabled = true;
          StartMenuBox.IsEnabled = true;
          DesktopBox.IsEnabled = true;
      }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>安装/卸载的实际动作。</summary>
internal static class Installer
{
    private const string AppExeName = "音匣.exe";
    private const string UninstallerName = "卸载 音匣.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundVault";

    public static bool HasPayload => Assembly.GetExecutingAssembly()
        .GetManifestResourceNames().Contains("SoundVault.App.exe");

    /// <summary>
    /// 规整安装目录：用户选/输了路径但结尾不是 SoundVault 时，自动补一层子目录。
    /// 例：D:\Apps → D:\Apps\SoundVault；已经带 SoundVault 就不再叠加；盘符根 D:\ → D:\SoundVault。
    /// </summary>
    public static string ResolveInstallDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";

        var trimmed = dir.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "";

        // 盘符根：C:\ 去尾部分隔符后是 "C:"，要补回来
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\SoundVault";

        var leaf = Path.GetFileName(trimmed);
        if (leaf.Equals("SoundVault", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return Path.Combine(trimmed, "SoundVault");
    }

    /// <summary>目标目录能不能写（不存在时看最近的已存在父目录）。</summary>
    public static bool CanWriteTo(string dir)
    {
        try
        {
            var d = new DirectoryInfo(dir);
            while (d is not null && !d.Exists) d = d.Parent;
            if (d is null) return false;
            var probe = Path.Combine(d.FullName, ".sv-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static void Install(string dir, bool createStartMenu, bool createDesktop, Action<double, string> progress)
    {
        Directory.CreateDirectory(dir);

        // 静默安装没有交互，直接尝试关掉正在运行的实例，避免文件被占用
            foreach (var name in new[] { "音匣", "SoundVault.App" })
            {
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        try { p.CloseMainWindow(); } catch { }
                    }
                }
                catch { }
            }
            System.Threading.Thread.Sleep(600);
            foreach (var name in new[] { "音匣", "SoundVault.App" })
            {
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                }
                catch { }
            }
            System.Threading.Thread.Sleep(300);

            progress(0.15, "正在释放主程序（约 76 MB）…");
        var appPath = Path.Combine(dir, AppExeName);
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundVault.App.exe")!)
        using (var dst = File.Create(appPath))
        {
            src.CopyTo(dst);
        }

        progress(0.35, "正在放置卸载程序…");
        var self = Environment.ProcessPath ?? "";
        var uninstaller = Path.Combine(dir, UninstallerName);
        if (self.Length > 0 && File.Exists(self) && !string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, uninstaller, overwrite: true);
        }

        progress(0.55, "正在建立快捷方式…");
        if (createStartMenu)
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "音匣 SoundVault");
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, "音匣 SoundVault.lnk"), appPath, dir);
        }
        if (createDesktop)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktop, "音匣 SoundVault.lnk"), appPath, dir);
        }

        progress(0.85, "正在登记到「应用和功能」…");
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "音匣 SoundVault");
            // 版本号跟着程序集走，别再写死了（v0.41 修）
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            key.SetValue("DisplayVersion", $"{ver.Major}.{ver.Minor}.{ver.Build}");
            key.SetValue("Publisher", "音匣");
            key.SetValue("InstallLocation", dir);
            key.SetValue("DisplayIcon", appPath);
            key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { key.SetValue("EstimatedSize", new FileInfo(appPath).Length / 1024, RegistryValueKind.DWord); } catch { }
        }

        progress(1.0, "完成。");
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t is null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = workDir;
        lnk.IconLocation = target;
        lnk.Description = "音匣 SoundVault";
        lnk.Save();
    }
}

internal static class Uninstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundVault";

    public static void Run(bool skipConfirm)
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        var answer = skipConfirm ? MessageBoxResult.Yes : MessageBox.Show(
            $"要从这个位置卸载音匣吗？\n\n{dir}\n\n配置与索引（%LocalAppData%\\SoundVault，含评分/标签）会保留。",
            "卸载 音匣 SoundVault", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

          // 只删指向自己这个目录的登记项，避免误删另一份安装（v0.54）
          try
          {
              using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
              var loc = key?.GetValue("InstallLocation") as string ?? "";
              if (loc.Length == 0 || string.Equals(loc.TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase))
                  Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
          }
          catch { }

        DeleteLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "音匣 SoundVault.lnk"));
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "音匣 SoundVault");
        try { if (Directory.Exists(startMenu)) Directory.Delete(startMenu, recursive: true); } catch { }

        // 主程序可能还在跑，先请它退出
        foreach (var p in Process.GetProcessesByName("音匣"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        var self = Environment.ProcessPath ?? "";
        foreach (var name in new[] { "音匣.exe", "SoundVault.App.exe" })
        {
            var f = Path.Combine(dir, name);
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        try
        {
            var un = Path.Combine(dir, "卸载 音匣.exe");
            if (File.Exists(un) && !string.Equals(un, self, StringComparison.OrdinalIgnoreCase)) File.Delete(un);
        }
        catch { }

        var leftover = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

        if (skipConfirm)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-uninstall.txt"),
                    "OK leftover=" + leftover + " dir=" + dir + Environment.NewLine);
            }
            catch { }
        }
        else
        {
            MessageBox.Show(leftover
                    ? "已卸载。目录里还有别的文件，就没有整个删掉。"
                    : "已卸载完成。",
                "音匣 SoundVault", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 收尾：把自己复制到临时目录，用它回来把整个安装目录（含卸载程序自己）删干净
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sv-uninstall-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
            File.Copy(self, tmp, overwrite: true);
            Process.Start(new ProcessStartInfo(tmp, "--cleanup \"" + dir + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void DeleteLink(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
