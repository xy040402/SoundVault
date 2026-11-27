using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>导出前的选项：命名模板 + 是否保留目录结构。带一个实时预览。</summary>
public partial class ExportDialog : Window
{
    private readonly AssetItem? _sample;
    private readonly string _libName;
    private readonly bool _defaultKeep;

    public string Template => string.IsNullOrWhiteSpace(TemplateBox.Text) ? ExportNames.DefaultTemplate : TemplateBox.Text.Trim();
    public bool KeepStructure => KeepBox.IsChecked == true;

    public ExportDialog(int count, AssetItem? sample, string libName, bool defaultKeep, string template)
    {
        InitializeComponent();

        _sample = sample;
        _libName = libName;
        _defaultKeep = defaultKeep;

        TitleText.Text = $"导出 {count} 个音效";
        TemplateBox.Text = template;
        KeepBox.IsChecked = defaultKeep;

        TemplateBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            else if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        };

        Loaded += (_, _) => { TemplateBox.Focus(); TemplateBox.SelectAll(); };
    }

    public static ExportDialog? Ask(Window? owner, int count, AssetItem? sample, string libName, bool defaultKeep, string template)
    {
        var dlg = new ExportDialog(count, sample, libName, defaultKeep, template);
        dlg.Owner = owner;
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void UpdatePreview()
    {
        if (_sample is null) { PreviewText.Text = ""; return; }

        try
        {
            var name = ExportNames.Expand(Template, 1, _sample, _libName);
            PreviewText.Text = "预览：" + name + Path.GetExtension(_sample.FilePath);
        }
        catch { PreviewText.Text = ""; }
    }

    private void Ok_Click(object sender, MouseButtonEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
