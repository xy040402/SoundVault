using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
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

    /// <summary>外观：主色（HEX）、面板强度、圆角、云母。取消时保持原值。</summary>
    public string AccentHex => _accentHex;
    private string _accentHex = "";
    public double GlassValue => GlassSlider.Value;
    public double RadiusValue => RadiusSlider.Value;
    public bool MicaEnabled => MicaBox.IsChecked == true;

    private Color _startAccent;
    private double _startGlass;
    private double _startRadius;
    private bool _suppressAccentSync;

    private readonly Store _store;
    private readonly Action<bool>? _onThemeChanged;
    private readonly bool _startDark;
    private bool _isDark;

    /// <summary>最终选择的深浅色。</summary>
    public bool UseDark => _isDark;

    public SettingsDialog(Store store, bool isDark, Action<bool>? onThemeChanged)
    {
        InitializeComponent();

        _store = store;
        _onThemeChanged = onThemeChanged;
        _startDark = isDark;
        SetDark(isDark, apply: false);

        TopmostBox.IsChecked = store.Topmost;
        PowerSaverBox.IsChecked = store.PowerSaver;
        TemplateBox.Text = string.IsNullOrWhiteSpace(store.ExportTemplate) ? ExportNames.DefaultTemplate : store.ExportTemplate;
        RulesBox.Text = string.Join(Environment.NewLine, NameCleaner.Normalize(store.CleanupRules));

        // 外观：预设色块 + 当前值
        _startAccent = AccentTheme.Parse(store.Accent) ?? AccentTheme.Base;
        _startGlass = store.Glass > 0 ? store.Glass : SurfaceTheme.Default;
        _startRadius = store.Radius > 0 ? store.Radius : SurfaceTheme.DefaultRadius;

        BuildPresets();
        _suppressAccentSync = true;
        _accentHex = AccentTheme.ToHex(_startAccent);
        HexBox.Text = _accentHex;
        _suppressAccentSync = false;
        Swatch.Background = new SolidColorBrush(_startAccent);

        GlassSlider.Value = _startGlass;
        RadiusSlider.Value = _startRadius;
        GlassText.Text = ((int)Math.Round(_startGlass * 100)) + "%";
        RadiusText.Text = _startRadius.ToString("0.00") + "×";
        MicaBox.IsChecked = store.Mica;

        PathText.Text = "配置：store.json（库/评分/标签归属/外观）\n索引：index.db（波形峰值与元信息，删了会自动重建）\n\n" + AppPaths.DataDir;
        PortableText.Text = AppPaths.ModeText;
        AssocText.Text = FileAssociation.IsRegistered ? "移除右键「添加到音匣」" : "注册右键「添加到音匣」";
        if (AppPaths.Portable) PortableBtn.Visibility = Visibility.Collapsed;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"音匣 SoundVault · {v?.ToString(3) ?? "0.17"}";
        AboutVersion.Text = "版本 " + (v?.ToString(3) ?? "?") + "　·　数据目录：" + AppPaths.DataDir;
        SelectNav(0);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    public static SettingsDialog? Open(Window? owner, Store store, bool isDark, Action<bool>? onThemeChanged, int page = 0)
    {
        var dlg = new SettingsDialog(store, isDark, onThemeChanged) { Owner = owner };
        dlg.SelectNav(page);
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

        // 主题一换，导航那几处颜色是代码里取的画刷，必须重新取一遍（否则浅色下字还是浅色）
        if (_navIndex >= 0) SelectNav(_navIndex);
    }
    // ── 外观（内嵌，不再单开窗口）────────────────────────
    private void BuildPresets()
    {
        PresetRow.Children.Clear();
        foreach (var (name, color) in AccentTheme.Presets)
        {
            var b = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                ToolTip = name,
                BorderThickness = new Thickness(1),
                BorderBrush = R("PanelBorder")
            };
            b.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetAccent(color);
            };
            PresetRow.Children.Add(b);
        }
    }

    private void SetAccent(Color c)
    {
        _suppressAccentSync = true;
        _accentHex = AccentTheme.ToHex(c);
        HexBox.Text = _accentHex;
        _suppressAccentSync = false;
        Swatch.Background = new SolidColorBrush(c);
        AccentTheme.Apply(c);          // 实时预览
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAccentSync) return;
        var c = AccentTheme.Parse(HexBox.Text);
        if (c is null) return;
        _accentHex = AccentTheme.ToHex(c.Value);
        Swatch.Background = new SolidColorBrush(c.Value);
        AccentTheme.Apply(c.Value);
    }

    private void Glass_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlassText is null) return;
        GlassText.Text = ((int)Math.Round(e.NewValue * 100)) + "%";
        SurfaceTheme.Apply(e.NewValue, RadiusSlider.Value);
    }

    private void Radius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusText is null) return;
        RadiusText.Text = e.NewValue.ToString("0.00") + "×";
        SurfaceTheme.Apply(GlassSlider.Value, e.NewValue);
    }

    private void ResetAccent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetAccent(AccentTheme.Base is var _ ? AccentTheme.Presets[0].Color : AccentTheme.Base);
    }

    private void RevertAppearance()
    {
        AccentTheme.Apply(_startAccent);
        SurfaceTheme.Apply(_startGlass, _startRadius);
    }
    private void Feedback_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/xy040402/SoundVault/issues/new/choose")
            { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenLogs2_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Directory.CreateDirectory(Log.Dir);
            Process.Start(new ProcessStartInfo(Log.Dir) { UseShellExecute = true });
        }
        catch { }
    }

    // ── 左侧导航 ─────────────────────────────────────────
    // 用 5 个 Border 而不是 ListBox：自绘模板的 ListBox 在这套样式下点击不选中（试过，不稳）
    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag?.ToString(), out var idx)) return;
        SelectNav(idx);
    }

    private int _navIndex = -1;

    private void Nav_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag?.ToString(), out var idx)) return;
        if (idx == _navIndex) return;          // 选中的那个不用再变
        if (fe is Border b) b.Background = R("CardHover");
    }

    private void Nav_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (!int.TryParse(fe.Tag?.ToString(), out var idx)) return;
        if (idx == _navIndex) return;
        if (fe is Border b) b.Background = Brushes.Transparent;
    }

    private void SelectNav(int idx)
    {
        _navIndex = idx;
        var borders = new[] { Nav0, Nav1, Nav2, Nav3, Nav4 };
        var bars = new[] { NavBar0, NavBar1, NavBar2, NavBar3, NavBar4 };
        var texts = new[] { NavText0, NavText1, NavText2, NavText3, NavText4 };

        for (int i = 0; i < borders.Length; i++)
        {
            bool on = i == idx;
            borders[i].Background = on ? R("AccentSoft") : Brushes.Transparent;
            bars[i].Background = on ? R("Accent") : Brushes.Transparent;
            texts[i].Foreground = on ? R("TextPrimary") : R("TextSecondary");
            texts[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        ShowPage(idx);
    }

    private void ShowPage(int i)
    {
        PageGeneral.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageExport.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageData.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>打开日志目录（%LocalAppData%\SoundVault\logs）。</summary>
    private void OpenLogs_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.IO.Directory.CreateDirectory(SoundVault.App.Services.Log.Dir);
            Process.Start(new ProcessStartInfo(SoundVault.App.Services.Log.Dir) { UseShellExecute = true });
            SoundVault.App.Services.Log.Info("设置", "用户打开日志目录");
        }
        catch (Exception ex)
        {
            SoundVault.App.Services.Log.Warn("设置", "打开日志目录失败", ex);
            if (PortableText != null) PortableText.Text = "打开日志目录失败：" + ex.Message;
        }
    }

    /// <summary>导出诊断包：日志 + 系统信息 + 设置，打成一个 zip。</summary>
    private void ExportDiag_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存诊断包",
            Filter = "Zip 压缩包|*.zip",
            FileName = $"音匣诊断-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var path = SoundVault.App.Services.Log.ExportDiagnostics(dlg.FileName);
            SoundVault.App.Services.Log.Info("设置", "用户导出诊断包 → " + path);
            if (PortableText != null) PortableText.Text = "诊断包已保存：" + path;
        }
        catch (Exception ex)
        {
            SoundVault.App.Services.Log.Error("设置", "导出诊断包失败", ex);
            if (PortableText != null) PortableText.Text = "导出诊断包失败：" + ex.Message;
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
        RevertAppearance();                                               // 取消：主色/面板强度/圆角还原
        DialogResult = false;
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
