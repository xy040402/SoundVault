using System.IO;
using System.Windows;
using System.Windows.Input;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>批量重命名：先给新旧名对照预览（重名的标红划掉），确认后才真的动文件。</summary>
public partial class BatchRenameDialog : Window
{
    public sealed class Row
    {
        public string Old { get; init; } = "";
        public string New { get; init; } = "";
        public bool Conflict { get; init; }
    }

    private readonly List<AssetItem> _items;
    private readonly string _libName;
    private readonly string _template;

    /// <summary>真正要改的清单：文件名（不含扩展名）。跳过项不在里面。</summary>
    public List<(AssetItem Item, string NewName)> Plan { get; } = new();
    public int Skipped { get; private set; }

    public BatchRenameDialog(List<AssetItem> items, string libName, string template)
    {
        InitializeComponent();

        _items = items;
        _libName = libName;
        _template = template;

        TitleText.Text = $"批量重命名 {items.Count} 个文件";
        TemplateBox.Text = template;

        TemplateBox.TextChanged += (_, _) => Rebuild();
        Rebuild();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };

        Loaded += (_, _) => { TemplateBox.Focus(); TemplateBox.SelectAll(); };
    }

    public static BatchRenameDialog? Ask(Window? owner, List<AssetItem> items, string libName, string template)
    {
        var dlg = new BatchRenameDialog(items, libName, template) { Owner = owner };
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void Rebuild()
    {
        Plan.Clear();
        var rows = new List<Row>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        int n = 0;

        foreach (var a in _items)
        {
            n++;
            var dir = Path.GetDirectoryName(a.FilePath) ?? "";
            var ext = Path.GetExtension(a.FilePath);

            string baseName;
            try { baseName = ExportNames.Expand(_template, n, a, _libName); }
            catch { baseName = a.DisplayName; }

            if (string.IsNullOrWhiteSpace(baseName)) baseName = a.DisplayName;

            var oldName = Path.GetFileNameWithoutExtension(a.FilePath);
            var newFull = Path.Combine(dir, baseName + ext);

            bool same = string.Equals(oldName, baseName, StringComparison.OrdinalIgnoreCase);
            bool exists = File.Exists(newFull) && !string.Equals(newFull, a.FilePath, StringComparison.OrdinalIgnoreCase);
            bool dup = !used.Add(baseName.ToLowerInvariant());

            bool conflict = same || exists || dup;
            rows.Add(new Row { Old = oldName + ext, New = baseName + ext + (dup ? "（清单内重名）" : ""), Conflict = conflict });

            if (conflict) skipped++;
            else Plan.Add((a, baseName));
        }

        PreviewList.ItemsSource = rows;
        Skipped = skipped;

        SummaryText.Text = Plan.Count == 0
            ? "没有可改的（全部同名或重名），换个模板试试"
            : $"将重命名 {Plan.Count} 个" + (skipped > 0 ? $"，跳过 {skipped} 个（同名/重名，已标红）" : "");
    }

    private void Ok_Click(object sender, MouseButtonEventArgs e)
    {
        if (Plan.Count == 0) return;
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
