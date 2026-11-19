using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>设置面板：外观入口、窗口置顶、省电、导出模板、数据目录、重建索引。</summary>
public partial class SettingsDialog : Window
{
    public bool Topmost2 => TopmostBox.IsChecked == true;
    public bool PowerSaver => PowerSaverBox.IsChecked == true;
    public string Template => string.IsNullOrWhiteSpace(TemplateBox.Text) ? ExportNames.DefaultTemplate : TemplateBox.Text.Trim();

    /// <summary>清洗规则（去掉空行）。</summary>
    public List<string> CleanupRules => RulesBox.Text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToList();

    /// <summary>用户点了「重建索引」。</summary>
    public bool RebuildRequested { get; private set; }

    /// <summary>用户点了「主色 / 外观」。</summary>
    public bool AppearanceRequested { get; private set; }

    private readonly Action<bool>? _onThemeChanged;
    private readonly bool _startDark;
    private bool _isDark;

    /// <summary>最终选择的深浅色。</summary>
    public bool UseDark => _isDark;

    public SettingsDialog(Store store, bool isDark, Action<bool>? onThemeChanged)
    {
        InitializeComponent();

        _onThemeChanged = onThemeChanged;
        _startDark = isDark;
        SetDark(isDark, apply: false);

        TopmostBox.IsChecked = store.Topmost;
        PowerSaverBox.IsChecked = store.PowerSaver;
        TemplateBox.Text = string.IsNullOrWhiteSpace(store.ExportTemplate) ? ExportNames.DefaultTemplate : store.ExportTemplate;
        RulesBox.Text = string.Join(Environment.NewLine, NameCleaner.Normalize(store.CleanupRules));

        PathText.Text = "配置：store.json（库/评分/标签归属/外观）\n索引：index.db（波形峰值与元信息，删了会自动重建）\n\n" + AppPaths.DataDir;
        PortableText.Text = AppPaths.ModeText;
        AssocText.Text = FileAssociation.IsRegistered ? "移除右键「添加到音匣」" : "注册右键「添加到音匣」";
        if (AppPaths.Portable) PortableBtn.Visibility = Visibility.Collapsed;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"音匣 SoundVault · {v?.ToString(3) ?? "0.17"}";

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    public static SettingsDialog? Open(Window? owner, Store store, bool isDark, Action<bool>? onThemeChanged)
    {
        var dlg = new SettingsDialog(store, isDark, onThemeChanged) { Owner = owner };
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        bool ok = dlg.ShowDialog() == true;
        // 「看一遍新手引导」是按"取消"关窗的，但要把对话框带回去，否则主窗口读不到请求
        return ok || dlg.ReplayOnboarding ? dlg : null;
    }

    private Brush R(string key) => key == "Transparent" ? Brushes.Transparent : (Brush)FindResource(key);

    private void Dark_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; SetDark(true); }
    private void Light_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; SetDark(false); }

    private void SetDark(bool dark, bool apply = true)
    {
        _isDark = dark;
        if (apply) _onThemeChanged?.Invoke(dark);

        DarkBtn.BorderBrush = R(dark ? "Accent" : "PanelBorder");
        DarkBtn.Background = dark ? R("PanelBg") : Brushes.Transparent;
        LightBtn.BorderBrush = R(dark ? "PanelBorder" : "Accent");
        LightBtn.Background = dark ? Brushes.Transparent : R("PanelBg");
    }
    private void Appearance_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AppearanceRequested = true;
        DialogResult = true;
        Close();
    }

    private void OpenData_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>便携模式（#45）：把配置复制到 exe 旁边的 data\\，重启后生效。</summary>
    /// <summary>文件关联（#49）：注册/移除资源管理器右键项。</summary>
    private void Assoc_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (FileAssociation.IsRegistered)
        {
            FileAssociation.Unregister(out var msg);
            AssocText.Text = FileAssociation.IsRegistered ? "移除右键「添加到音匣」" : "注册右键「添加到音匣」";
            PortableText.Text = msg;
        }
        else
        {
            FileAssociation.Register(out var msg);
            AssocText.Text = FileAssociation.IsRegistered ? "移除右键「添加到音匣」" : "注册右键「添加到音匣」";
            PortableText.Text = msg;
        }
    }

    /// <summary>把 store.json / index.db 备份到用户选的文件夹（带时间戳子目录）。</summary>
    private void Backup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择备份到哪里" };
        if (dlg.ShowDialog() != true) return;

        var src = SoundVault.App.Services.AppPaths.DataDir;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var target = System.IO.Path.Combine(dlg.FolderName, "音匣备份_" + stamp);
        try
        {
            System.IO.Directory.CreateDirectory(target);
            int n = 0;
            foreach (var name in new[] { "store.json", "index.db" })
            {
                var from = System.IO.Path.Combine(src, name);
                if (!System.IO.File.Exists(from)) continue;
                System.IO.File.Copy(from, System.IO.Path.Combine(target, name), overwrite: true);
                n++;
            }
            if (PortableText != null)
                PortableText.Text = $"已备份 {n} 个文件到：{target}";
        }
        catch (Exception ex)
        {
            if (PortableText != null) PortableText.Text = "备份失败：" + ex.Message;
        }
    }
    private void Portable_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!AppPaths.SwitchToPortable(out var msg))
        {
            PortableText.Text = msg;
            return;
        }

        PortableText.Text = msg;
        PortableBtn.Visibility = Visibility.Collapsed;
    }

    private void Rebuild_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RebuildRequested = true;
        DialogResult = true;
        Close();
    }

    private void Ok_Click(object sender, MouseButtonEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>用户想再看一遍新手引导：关掉设置，让主窗口重新播。</summary>
    public bool ReplayOnboarding { get; private set; }

    private void ReplayOnboarding_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ReplayOnboarding = true;
        DialogResult = false;      // 不改任何设置，直接关掉
    }

    private void Cancel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isDark != _startDark) _onThemeChanged?.Invoke(_startDark);   // 取消：主题还原
        DialogResult = false;
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
