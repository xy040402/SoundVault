using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>
/// 外观：主色编辑器。预设 / HEX / RGB / HSV 三向互通，改哪边都能同步到另外两边和预览。
/// 关闭时返回选中的颜色（取消返回 null）。
/// </summary>
public partial class ThemeDialog : Window
{
    private bool _sync;          // 防止三方互相触发
    private bool _syncGlass;     // 初始化滑杆时别触发
    private Color _color;
    private double _startGlass;
    private double _startRadius = 1.0;

    public Color Result { get; private set; }
    public bool PowerSaver => PowerSaverBox.IsChecked == true;
    public double Glass => GlassSlider.Value;
    public double Radius => RadiusSlider.Value;
    public bool Mica => MicaBox.IsChecked == true;

    public ThemeDialog()
    {
        InitializeComponent();
        BuildPresets();
        SetColor(AccentTheme.Base);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };

        Loaded += (_, _) => HexBox.Focus();
    }

    /// <summary>打开对话框；确定时返回颜色，取消返回 null。</summary>
    public static ThemeDialog? Pick(Window? owner, Color current, bool powerSaver, double glass, double radius, bool mica)
    {
        var dlg = new ThemeDialog { Owner = owner };
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.SetColor(current);
        dlg.PowerSaverBox.IsChecked = powerSaver;
        dlg.MicaBox.IsChecked = mica;

        dlg._syncGlass = true;
        dlg.GlassSlider.Value = glass;
        dlg._syncGlass = false;
        dlg._startGlass = glass;
        dlg._startRadius = radius;

        dlg._syncGlass = true;
        dlg.RadiusSlider.Value = radius;
        dlg._syncGlass = false;
        dlg.RadiusText.Text = ((int)Math.Round(radius * 100)) + "%";
        dlg.GlassText.Text = ((int)Math.Round(glass * 100)) + "%";

        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void BuildPresets()
    {
        foreach (var (name, color) in AccentTheme.Presets)
        {
            var dot = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                ToolTip = name,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent
            };
            var c = color;
            dot.MouseLeftButtonDown += (_, e) => { e.Handled = true; SetColor(c); };
            PresetRow.Children.Add(dot);
        }
    }

    // ── 颜色 → 控件 ────────────────────────────────────────
    private void SetColor(Color c)
    {
        _color = c;
        _sync = true;
        try
        {
            RSlider.Value = c.R;
            GSlider.Value = c.G;
            BSlider.Value = c.B;

            var (h, s, v) = AccentTheme.ToHsv(c);
            HSlider.Value = h;
            SSlider.Value = s * 100;
            VSlider.Value = v * 100;

            HexBox.Text = AccentTheme.ToHex(c);
        }
        finally { _sync = false; }

        UpdateLabels();
        UpdatePreview();
    }

    private void UpdateLabels()
    {
        RText.Text = _color.R.ToString();
        GText.Text = _color.G.ToString();
        BText.Text = _color.B.ToString();

        var (h, s, v) = AccentTheme.ToHsv(_color);
        HText.Text = h.ToString("0");
        SText.Text = (s * 100).ToString("0") + "%";
        VText.Text = (v * 100).ToString("0") + "%";
    }

    private void UpdatePreview()
    {
        var brush = new SolidColorBrush(_color);
        Swatch.Background = brush;

        PrevButton.Background = brush;
        if (PrevButton.Child is TextBlock bt) bt.Foreground = new SolidColorBrush(AccentTheme.OnColor(_color));

        PrevTag.BorderBrush = brush;
        PrevTag.Background = new SolidColorBrush(Color.FromArgb(0x24, _color.R, _color.G, _color.B));
        if (PrevTag.Child is TextBlock tt) tt.Foreground = brush;

        PrevStars.Foreground = brush;
    }

    // ── 控件 → 颜色 ────────────────────────────────────────
    private void Rgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        SetColor(Color.FromRgb((byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value));
    }

    private void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        SetColor(AccentTheme.FromHsv(HSlider.Value, SSlider.Value / 100.0, VSlider.Value / 100.0));
    }

    /// <summary>拖面板强度：立刻套到整个界面（对话框自己也跟着变，等于实时预览）。</summary>
    private void Radius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncGlass) return;
        RadiusText.Text = ((int)Math.Round(e.NewValue * 100)) + "%";
        SurfaceTheme.Apply(GlassSlider.Value, e.NewValue);
    }
    private void Glass_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncGlass) return;
        GlassText.Text = ((int)Math.Round(e.NewValue * 100)) + "%";
        SurfaceTheme.Apply(e.NewValue, RadiusSlider.Value);
    }
    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        var parsed = AccentTheme.Parse(HexBox.Text);
        if (parsed is null) return;
        SetColor(parsed.Value);
    }

    // ── 按钮 ───────────────────────────────────────────────
    private void Ok_Click(object sender, MouseButtonEventArgs e)
    {
        Result = _color;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, MouseButtonEventArgs e)
    {
        SurfaceTheme.Apply(_startGlass, _startRadius);   // 取消就把面板强度和圆角还原
        DialogResult = false;
        Close();
    }

    private void Reset_Click(object sender, MouseButtonEventArgs e) => SetColor(AccentTheme.Presets[0].Color);

    // ── 主题文件（.theme.json）────────────────────────────
    private void Export_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出音匣主题",
            Filter = "音匣主题 (*.theme.json)|*.theme.json|JSON (*.json)|*.json",
            FileName = "音匣主题-" + AccentTheme.ToHex(_color).TrimStart('#') + ".theme.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var json = "{\n  \"accent\": \"" + AccentTheme.ToHex(_color) + "\",\n  \"powerSaver\": " +
                       (PowerSaver ? "true" : "false") + "\n}\n";
            System.IO.File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
            MessageBox.Show(this, "已导出：" + dlg.FileName, "音匣", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "音匣", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Import_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入音匣主题",
            Filter = "音匣主题 (*.theme.json;*.json)|*.theme.json;*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var text = System.IO.File.ReadAllText(dlg.FileName);
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"accent\"\\s*:\\s*\"(#[0-9A-Fa-f]{3,8})\"");
            var parsed = m.Success ? AccentTheme.Parse(m.Groups[1].Value) : null;
            if (parsed is null)
            {
                MessageBox.Show(this, "这个文件里没有找到 accent 颜色。", "音匣", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetColor(parsed.Value);
            var p = System.Text.RegularExpressions.Regex.Match(text, "\"powerSaver\"\\s*:\\s*(true|false)");
            if (p.Success) PowerSaverBox.IsChecked = p.Groups[1].Value == "true";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "音匣", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
