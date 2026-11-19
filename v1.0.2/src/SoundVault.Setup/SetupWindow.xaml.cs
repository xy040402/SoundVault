using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace SoundVault.Setup;

/// <summary>
/// 安装向导（4 步）：欢迎 → 选择位置 → 安装中 → 完成。
/// 布局照 NSIS MUI2 那种：左侧品牌竖栏 + 右侧内容 + 底部按钮条。
/// </summary>
public partial class SetupWindow : Window
{
    private const string AppExeName = "音匣.exe";
    private const string UninstallerName = "卸载 音匣.exe";

    private int _step;                    // 0 欢迎 / 1 位置 / 2 安装中 / 3 完成
    private string _installDir = "";
    private bool _payloadMissing;

    public SetupWindow()
    {
        InitializeComponent();

        SideVersion.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\SoundVault");

        SetStep(0);

        if (!Installer.HasPayload)
        {
            _payloadMissing = true;
            SetStep(1);
            Notice(@"这个安装包是坏的：里面没有主程序（打包时 _local\dist\音匣.exe 不存在）。请重新下载。");
        }
    }

    /// <summary>提权重启后：预填安装位置；autoStart=true 就直接开始装。</summary>
    public void PresetTarget(string dir, bool autoStart)
    {
        PathBox.Text = dir;
        _installDir = dir;

        if (!autoStart) { SetStep(1); return; }

        Loaded += async (_, _) =>
        {
            SetStep(1);
            await RunInstallAsync();
        };
    }

    // ── 步骤切换 ───────────────────────────────────────────

    private void SetStep(int s)
    {
        _step = s;

        PageWelcome.Visibility = s == 0 ? Visibility.Visible : Visibility.Collapsed;
        PagePath.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageInstall.Visibility = s == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageFinish.Visibility = s == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageError.Visibility = s == 4 ? Visibility.Visible : Visibility.Collapsed;
        PagePrompt.Visibility = s == 5 ? Visibility.Visible : Visibility.Collapsed;

        BackBtn.Visibility = (s == 1 || s == 4) ? Visibility.Visible : Visibility.Collapsed;

        NextBtn.Content = s switch
        {
            0 => "下一步",
            1 => "安装",
            3 => "完成",
            4 => "重试",
            _ => "请稍候…"
        };
        NextBtn.IsEnabled = s != 2 && s != 5 && !(_payloadMissing && s == 1);
        NextBtn.Visibility = s == 5 ? Visibility.Collapsed : Visibility.Visible;
        CancelBtn.Visibility = s == 5 ? Visibility.Collapsed : Visibility.Visible;

        CancelBtn.Content = s == 3 ? "关闭" : "取消";
        CancelBtn.IsEnabled = s != 2;
        CloseBtn.IsEnabled = s != 2;
    }

    private void Notice(string text, bool warn = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            return;
        }
        NoticeText.Text = text;
        NoticeText.Foreground = warn
            ? System.Windows.Media.Brushes.Goldenrod
            : System.Windows.Media.Brushes.LightGreen;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1) SetStep(0);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0:
                SetStep(1);
                PathBox.Focus();
                break;
            case 1:
                _ = RunInstallAsync();
                break;
            case 3:
                FinishAndClose();
                break;
            case 4:
                _ = RunInstallAsync();       // 重试
                break;
        }
    }

    private static string ShortcutSummary()
    {
        var parts = new List<string>();
        parts.Add(Installer.StartMenuOk ? "开始菜单 ✔" : "开始菜单 ✘ 没建成");
        parts.Add(Installer.DesktopOk ? "桌面 ✔" : "桌面 ✘ 没建成");
        return "快捷方式：" + string.Join("  ·  ", parts);
    }

    private void FinishAndClose()
    {
        if (LaunchBox.IsChecked == true && _installDir.Length > 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(_installDir, AppExeName)) { UseShellExecute = true });
            }
            catch { }
        }
        SetupLog.Info("安装界面", "用户点完成，关闭安装器");
        Close();
    }

    // ── 安装 ───────────────────────────────────────────────

    private async Task RunInstallAsync()
    {
        var dir = Installer.ResolveInstallDir(PathBox.Text.Trim());
        PathBox.Text = dir;
        if (dir.Length == 0)
        {
            Notice("请选择安装位置。");
            SetStep(1);
            return;
        }

        Notice("");
        SetStep(2);
        InstallTargetText.Text = "目标：" + dir;
        LogText.Text = "准备中…";
        ProgressFill.Width = 0;
        SetupLog.Info("安装界面", $"点击安装 目标={dir}");

        await Task.Yield();      // 先把"安装中"这一页画出来，再做检查

        // 1) 有正在运行的实例？问一句，关掉才继续
        if (!await EnsureAppClosedAsync())
        {
            SetStep(1);
            Notice("已取消：请先关闭正在运行的音匣，再点「安装」。");
            return;
        }

        // 2) 旧版本装在别的位置？提示先卸载
        if (TryGetExisting(out var oldPath, out var oldVersion) && !SameDir(oldPath, dir))
        {
            if (!await RemoveOldInstallAsync(oldPath, oldVersion, dir))
            {
                SetStep(1);
                Notice("已取消：旧版本没有卸载。");
                return;
            }
        }

        // 3) 目标目录不可写（Program Files 之类）→ 提权重启自己，回到"位置"这一步
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
                SetupLog.Info("提权", "目标不可写，重启自身请求管理员 → " + dir);
                Process.Start(psi);
                Application.Current.Shutdown();
                return;
            }
            catch (Exception ex)
            {
                SetupLog.Error("提权", "提权被拒绝（用户取消 UAC？）", ex);
                SetStep(1);
                Notice("需要管理员权限才能装到这里，但提权被拒绝：" + ex.Message);
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
            Thread.Sleep(120);
        }

        try
        {
            // 验收钩子：SV_SETUP_FAIL=1 时模拟一次"磁盘满"，用来看失败页与诊断信息
            if (Environment.GetEnvironmentVariable("SV_SETUP_FAIL") == "1")
                throw new IOException("测试：模拟写入失败（磁盘已满）", unchecked((int)0x80070027));

            Installer.Install(dir,
                createStartMenu: StartMenuBox.IsChecked == true,
                createDesktop: DesktopBox.IsChecked == true,
                progress: (d, t) => Report(d, t));

            ProgressFill.Width = trackWidth;
            _installDir = dir;
            FinishPathText.Text = "音匣 已安装到：" + dir;
            FinishShortcutText.Text = ShortcutSummary();
            SetupLog.Info("安装界面", "安装完成 ✔ " + dir);
            SetStep(3);
        }
        catch (Exception ex)
        {
            SetupLog.Error("安装界面", "安装失败", ex);
            ShowFailure(ex, dir);
        }
    }

    // ── 失败处理（P7）────────────────────────────────────

    private Exception? _lastError;
    private string _lastDir = "";

    /// <summary>把异常翻译成人话：先看是不是某一步失败，再按错误码/异常类型给建议。</summary>
    private static string Friendly(Exception ex)
    {
        var step = (ex as Installer.InstallStepException)?.Step;
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        int code = inner.HResult;

        string why = inner switch
        {
            UnauthorizedAccessException => "没有写入权限。这个目录受系统保护，或者被杀毒软件拦住了。",
            PathTooLongException => "安装路径太长，换个短一点的目录试试。",
            DirectoryNotFoundException => "目标目录不存在而且建不出来，换一个位置试试。",
            IOException io when code == unchecked((int)0x80070027) || code == unchecked((int)0x80070070)
                => "磁盘空间不足。清理一下再装，安装需要约 200 MB。",
            IOException io when code == unchecked((int)0x80070020)
                => "文件正被别的程序占用（可能是还在运行的音匣，或杀毒软件正在扫描）。关掉它们再重试。",
            IOException io when code == unchecked((int)0x80070005)
                => "写入被拒绝（拒绝访问）。可能是杀毒软件拦截，或者这个目录需要管理员权限。",
            NullReferenceException when step == "释放主程序"
                => "安装包里没有主程序资源（包可能下载不完整）。请重新下载安装包。",
            IOException io => "读写文件时出错：" + io.Message,
            _ => inner.Message
        };

        return step is null ? why : $"在「{step}」这一步失败。\n{why}";
    }

    private void ShowFailure(Exception ex, string dir)
    {
        _lastError = ex;
        _lastDir = dir;

        ErrorReason.Text = Friendly(ex);
        // 详情只给"类型 + 错误码 + 消息"，完整堆栈留在日志和复制出来的诊断信息里
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        ErrorDetail.Text = $"{inner.GetType().Name}（0x{inner.HResult:X8}）：{inner.Message}";

        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(dir));
            if (drive is not null)
            {
                var d = new DriveInfo(drive);
                ErrorDetail.Text += $"\n\n磁盘 {drive} 可用：{d.AvailableFreeSpace / 1024 / 1024} MB";
            }
        }
        catch { }

        CopiedHint.Visibility = Visibility.Collapsed;
        SetStep(4);
    }

    /// <summary>拼一段可以直接粘给作者的诊断信息。</summary>
    private string BuildDiagnostic(string dir, Exception? ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("音匣 SoundVault 安装失败诊断");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("安装器版本：" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"));
        sb.AppendLine("系统：" + Environment.OSVersion.VersionString + " / " + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
        sb.AppendLine("管理员：" + (SetupLog.IsElevated() ? "是" : "否"));
        sb.AppendLine("目标目录：" + dir);

        if (ex is not null)
        {
            sb.AppendLine("失败原因：" + Friendly(ex).Replace("\n", " "));
            sb.AppendLine("异常：" + ex.GetType().FullName + " (0x" + ex.HResult.ToString("X8") + ")：" + ex.Message);
            if (ex is Installer.InstallStepException se) sb.AppendLine("失败步骤：" + se.Step);
            sb.AppendLine();
            sb.AppendLine("---- 完整异常 ----");
            sb.AppendLine(ex.ToString());
        }

        sb.AppendLine("日志目录：" + SetupLog.Dir);
        foreach (var f in SetupLog.RecentFiles()) sb.AppendLine("  " + f);
        sb.AppendLine();
        sb.AppendLine("---- 最近日志 ----");
        sb.AppendLine(SetupLog.Tail(40));
        return sb.ToString();
    }

    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = BuildDiagnostic(_lastDir, _lastError);
            Clipboard.SetText(text);
            SetupLog.Info("安装界面", $"已复制诊断信息 长度={text.Length}");
            CopiedHint.Visibility = Visibility.Visible;

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { CopiedHint.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            SetupLog.Error("安装界面", "复制诊断信息失败", ex);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SetupLog.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SetupLog.Dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetupLog.Error("安装界面", "打开日志目录失败", ex);
        }
    }

    // ── 向导内的询问（替代系统 MessageBox）───────────────

    private TaskCompletionSource<bool>? _promptTcs;

    /// <summary>在向导里问一句，返回用户点了"继续"还是"取消"。</summary>
    private Task<bool> AskAsync(string title, string text, string yes, string no)
    {
        PromptTitle.Text = title;
        PromptText.Text = text;
        PromptYes.Content = yes;
        PromptNo.Content = no;

        _promptTcs = new TaskCompletionSource<bool>();
        SetStep(5);
        return _promptTcs.Task;
    }

    private void PromptYes_Click(object sender, RoutedEventArgs e)
    {
        SetStep(2);
        _promptTcs?.TrySetResult(true);
    }

    private void PromptNo_Click(object sender, RoutedEventArgs e)
    {
        _promptTcs?.TrySetResult(false);
    }

    // ── 窗口拖动 / 关闭 ─────────────────────────────────────

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
    /// <summary>有正在运行的实例就在向导里问一句，同意就关掉。</summary>
    private async Task<bool> EnsureAppClosedAsync()
    {
        var procs = RunningApp();
        if (procs.Length == 0) return true;

        var ok = await AskAsync("音匣正在运行",
            $"检测到 {procs.Length} 个音匣进程。\n\n继续安装要先关掉它，否则文件被占用会失败。",
            "关闭音匣并继续", "取消安装");
        if (!ok) return false;

        foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
        await Task.Delay(800);
        foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch { } }
        await Task.Delay(500);
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
    /// <summary>旧版本装在别处：在向导里问一句，同意就先把旧版本卸掉。</summary>
    private async Task<bool> RemoveOldInstallAsync(string oldPath, string oldVersion, string newPath)
    {
        var ok = await AskAsync("检测到已安装的旧版本",
            $"旧版本 {(oldVersion.Length > 0 ? oldVersion : "（版本未知）")} 装在：\n{oldPath}\n\n" +
            $"要先卸载它，再安装到：\n{newPath}\n\n" +
            "评分、标签、集合等用户数据不会被删除。",
            "卸载旧版本并继续", "取消");
        if (!ok) return false;

        var uninstaller = Path.Combine(oldPath, UninstallerName);
        if (!File.Exists(uninstaller))
        {
            SetStep(1);
            Notice($"找不到旧版本的卸载程序：{uninstaller}。请手动删掉那个文件夹再试。");
            return false;
        }

        var psi = new ProcessStartInfo(uninstaller, "--uninstall --yes") { UseShellExecute = true };
        if (!Installer.CanWriteTo(oldPath)) psi.Verb = "runas";      // 装在 Program Files 的要提权
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            SetStep(1);
            Notice("启动旧版本卸载程序失败：" + ex.Message);
            return false;
        }

        SetStep(2);
        LogText.Text = "正在卸载旧版本…";
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        for (int i = 0; i < 60 && Directory.Exists(oldPath); i++)
        {
            await Task.Delay(400);
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
        if (dlg.ShowDialog() == true)
            PathBox.Text = Installer.ResolveInstallDir(dlg.FolderName);   // 选完就补 SoundVault（v0.43 修）
    }
}

/// <summary>安装/卸载的实际动作。</summary>
internal static class Installer
{
    /// <summary>这一次安装里两个快捷方式的实际结果，完成页要如实显示。</summary>
    public static bool StartMenuOk { get; private set; }
    public static bool DesktopOk { get; private set; }
    public static string StartMenuPath { get; private set; } = "";
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

    /// <summary>安装失败时带上"卡在哪一步"，UI 才能说人话。</summary>
    public sealed class InstallStepException : Exception
    {
        public string Step { get; }
        public InstallStepException(string step, Exception inner) : base($"{step}失败：{inner.Message}", inner)
        {
            Step = step;
        }
    }

    private static void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            SetupLog.Error("安装", $"步骤失败：{name}", ex);
            throw new InstallStepException(name, ex);
        }
    }

    public static void Install(string dir, bool createStartMenu, bool createDesktop, Action<double, string> progress)
    {
        SetupLog.Info("安装", $"写入目标={dir}");

        Step("准备安装目录", () => Directory.CreateDirectory(dir));

        // 空间不够早点说，别写到一半失败
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                long need = 200L * 1024 * 1024;
                if (drive.AvailableFreeSpace < need)
                    throw new IOException($"磁盘空间不足：{root} 只剩 {drive.AvailableFreeSpace / 1024 / 1024} MB，安装需要约 200 MB。");
            }
        }
        catch (IOException) { throw; }
        catch { }   // 拿不到磁盘信息就算了，不影响安装

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
        SetupLog.Info("安装", "开始释放主程序 → " + appPath);

        Step("释放主程序", () =>
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundVault.App.exe")!;
            using var dst = File.Create(appPath);
            src.CopyTo(dst);
        });

        SetupLog.Info("安装", "主程序写入完成 大小=" + new FileInfo(appPath).Length);
        progress(0.35, "正在放置卸载程序…");

        var self = Environment.ProcessPath ?? "";
        var uninstaller = Path.Combine(dir, UninstallerName);
        Step("放置卸载程序", () =>
        {
            if (self.Length > 0 && File.Exists(self) && !string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, overwrite: true);
        });

        SetupLog.Info("安装", $"卸载程序已放置 开始菜单={createStartMenu} 桌面={createDesktop}");
        progress(0.55, "正在建立快捷方式…");

        Step("创建开始菜单快捷方式", () =>
        {
            if (!createStartMenu) return;
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            // 直接放在「开始菜单\程序」根下：按 Win 打开就是「所有应用」里的一条，不用再点开同名文件夹
            var legacyFolder = Path.Combine(programs, "音匣 SoundVault");
            try { if (Directory.Exists(legacyFolder)) Directory.Delete(legacyFolder, recursive: true); } catch { }
            var lnk = Path.Combine(programs, "音匣 SoundVault.lnk");
            StartMenuOk = CreateShortcut(lnk, appPath, dir);
            StartMenuPath = lnk;
            if (!StartMenuOk) SetupLog.Warn("安装", "开始菜单快捷方式没有建成 → " + lnk);
        });
        Step("创建桌面快捷方式", () =>
        {
            if (!createDesktop) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnk2 = Path.Combine(desktop, "音匣 SoundVault.lnk");
            DesktopOk = CreateShortcut(lnk2, appPath, dir);
            if (!DesktopOk) SetupLog.Warn("安装", "桌面快捷方式没有建成 → " + lnk2);
        });

        SetupLog.Info("安装", "快捷方式完成，开始写注册表");
        progress(0.85, "正在登记到「应用和功能」…");

        Step("写入「应用和功能」登记项", () =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "音匣 SoundVault");
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            key.SetValue("DisplayVersion", $"{ver.Major}.{ver.Minor}.{ver.Build}");
            key.SetValue("Publisher", "音匣");
            key.SetValue("InstallLocation", dir);
            key.SetValue("DisplayIcon", appPath);
            key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { key.SetValue("EstimatedSize", new FileInfo(appPath).Length / 1024, RegistryValueKind.DWord); } catch { }
        });

        SetupLog.Info("安装", $"全部完成 dir={dir}");
        progress(1.0, "完成。");
    }

    /// <summary>
    /// 建 .lnk。以前只用 WScript.Shell（WSH 被系统禁用时它返回 null，于是"什么都没建、也不报错"）。
    /// 现在先用 IShellLink，失败了再退回 WScript，最后校验文件是否真的存在。
    /// </summary>
    private static bool CreateShortcut(string lnkPath, string target, string workDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(target);
            link.SetWorkingDirectory(workDir);
            link.SetDescription("音匣 SoundVault");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);

            if (File.Exists(lnkPath)) return true;
            SetupLog.Warn("安装", "IShellLink 没有写出文件，改用 WScript 再试 → " + lnkPath);
        }
        catch (Exception ex)
        {
            SetupLog.Warn("安装", "IShellLink 建快捷方式失败，改用 WScript：" + ex.Message);
        }

        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null)
            {
                SetupLog.Error("安装", "系统里没有 WScript.Shell（WSH 可能被禁用），快捷方式建不出来");
                return false;
            }
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            lnk.IconLocation = target;
            lnk.Description = "音匣 SoundVault";
            lnk.Save();
        }
        catch (Exception ex)
        {
            SetupLog.Error("安装", "WScript 建快捷方式也失败", ex);
            return false;
        }

        return File.Exists(lnkPath);
    }

    // ── IShellLink 互操作（不依赖 WSH）─────────────────────
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
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

