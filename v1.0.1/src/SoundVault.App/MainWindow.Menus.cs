using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundVault.App.Audio;
using SoundVault.App.Controls;
using SoundVault.App.Dialogs;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>右键菜单 / 导出 / 重命名 / 复制（拆分出来的，逻辑未改）</summary>
public partial class MainWindow
{
    private void ToggleTag(AssetItem a, string tag)
    {
        var list = a.Tags.ToList();
        int hit = list.FindIndex(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (hit >= 0) list.RemoveAt(hit);
        else list.Add(tag);

        try { _db?.SetTags(a.FilePath, list); }
        catch (Exception ex) { CrashLog("写标签失败：" + ex.Message); }

        a.SetTags(list);

        if (_searchMode) ApplyFilter();
    }
    private void AssetCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _canDrag = false;
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;

        if (!_selected.Contains(a))          // 右键先选中它（已在多选里就保留多选）
        {
            _selection.SelectOnly(a);   // #51
            _selected.Clear();
            _selected.Add(a);
            _anchor = a;
            SyncSelection();
        }

        var menu = BuildAssetMenu(a);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private ContextMenu BuildAssetMenu(AssetItem a)
    {
        var menu = new ContextMenu();

        // 集合（#8）：一次把选中的都加进去
        var addTo = new MenuItem { Header = _selected.Count > 1 ? $"把选中的 {_selected.Count} 个加入集合" : "加入到集合" };
        foreach (var c in _store.Collections)
        {
            var col = c;
            var mi = new MenuItem { Header = $"{col.Name}（{col.Paths.Count}）" };
            mi.Click += (_, _) => AddToCollection(col);
            addTo.Items.Add(mi);
        }
        if (addTo.Items.Count > 0) addTo.Items.Add(new Separator());
        var newCol = new MenuItem { Header = "新建集合…" };
        newCol.Click += (_, _) => AddToCollection(null);
        addTo.Items.Add(newCol);
        menu.Items.Add(addTo);

        // 在集合视图里：可以把音效从集合里踢出去（v0.56）
        if (_viewKind == ViewKind.Collection && _currentCollection is not null)
        {
            var targets = _selection.Count > 0 ? _selection.Items.ToList() : new List<AssetItem> { a };
            var kick = new MenuItem
            {
                Header = targets.Count > 1 ? $"从集合中移除（{targets.Count} 个）" : "从集合中移除"
            };
            kick.Click += (_, _) =>
            {
                var col = _currentCollection;
                if (col is null) return;
                foreach (var it in targets)
                    col.Paths.RemoveAll(p => p.Equals(it.FilePath, StringComparison.OrdinalIgnoreCase));
                _store.Save();
                RefreshCollections();
                LoadCollectionView(col);
                ShowToast(targets.Count > 1 ? $"已从集合移除 {targets.Count} 个" : "已从集合移除");
            };
            menu.Items.Add(kick);
            menu.Items.Add(new Separator());
        }

        var fav = new MenuItem { Header = a.IsFavorite ? "取消收藏" : "收藏", IsCheckable = true, IsChecked = a.IsFavorite };
        fav.Click += (_, _) => ToggleFavorite(a);
        menu.Items.Add(fav);

        var rate = new MenuItem { Header = "评分" };
        for (int i = 1; i <= 5; i++)
        {
            int n = i;
            var mi = new MenuItem { Header = new string('★', i), IsCheckable = true, IsChecked = a.Rating == n };
            mi.Click += (_, _) => SetRating(a, a.Rating == n ? 0 : n);
            rate.Items.Add(mi);
        }
        rate.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除评分", IsEnabled = a.Rating > 0 };
        clear.Click += (_, _) => SetRating(a, 0);
        rate.Items.Add(clear);
        menu.Items.Add(rate);

        var tags = new MenuItem { Header = "标签" };
        foreach (var mi in BuildTagItems(a)) tags.Items.Add(mi);
        menu.Items.Add(tags);

        menu.Items.Add(new Separator());

        var metaItem = new MenuItem { Header = "元数据…" };
        metaItem.Click += (_, _) => ShowMetadata(a);
        menu.Items.Add(metaItem);
        var copyFiles = new MenuItem { Header = _selected.Count > 1 ? $"复制这 {_selected.Count} 个文件" : "复制文件" };
        copyFiles.Click += (_, _) => CopySelection(toFiles: true);
        menu.Items.Add(copyFiles);

        var copy = new MenuItem { Header = "复制文件路径" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(a.FilePath); }
            catch (Exception ex) { CrashLog("复制路径失败：" + ex.Message); ShowToast("复制失败"); }
        };
        menu.Items.Add(copy);

        var reveal = new MenuItem { Header = "在资源管理器中显示" };
        reveal.Click += (_, _) => RevealFile(a.FilePath);
        menu.Items.Add(reveal);

        menu.Items.Add(new Separator());

        if (_selected.Count > 0)
        {
            var rename = new MenuItem { Header = $"批量重命名选中的 {_selected.Count} 个…" };
            rename.Click += (_, _) => BatchRename();
            menu.Items.Add(rename);
        }

        var export = new MenuItem { Header = _selected.Count > 1 ? $"导出选中的 {_selected.Count} 个" : "导出这个音效" };
        export.Click += (_, _) => _ = ExportAsync();
        menu.Items.Add(export);

        return menu;
    }
    private void TagLine_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _canDrag = false;
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;

        var menu = new ContextMenu();
        foreach (var mi in BuildTagItems(a)) menu.Items.Add(mi);

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private async Task AddExternalFileAsync(string file)
    {
        try
        {
            if (!File.Exists(file)) { ShowToast("文件不存在：" + Path.GetFileName(file)); return; }

            var dir = Path.GetDirectoryName(file);
            if (dir is null) return;

            // 已经在这个库里面？直接跳过去
            var root = _libs.Keys
                .Where(k => dir.StartsWith(k.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();

            if (root is null)
            {
                _pendingSelect = file;
                await AddLibraryAsync(dir, select: false);
                root = _libs.Keys
                    .Where(k => dir.StartsWith(k.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => k.Length)
                    .FirstOrDefault();
            }

            if (root is null) return;

            // 先按层级找到文件所在的文件夹节点（侧栏树里），没有就退回库根
            var node = FindFolderNode(dir);
            if (node is not null && !string.Equals(node.Path, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowLibrary(root);
                await Task.Delay(250);
                SyncSidebarSelection(node);
                OpenFolder(node);
            }
            else
            {
                ShowLibrary(root);
            }

            SelectByPath(file);
        }
        catch (Exception ex)
        {
            CrashLog("添加外部文件失败：" + ex);
            ShowToast("添加失败");
        }
    }
    private void ShowMetadata(AssetItem a)
    {
        var m = MetaOf(a.FilePath);

        if (!m.HasAnything)
        {
            MessageDialog.Show(this, "元数据",
                $"「{a.DisplayName}」里没有找到 BWF(bext) / LIST-INFO / iXML 元数据。\n\n" +
                "很多素材包只写文件名，专业录音素材才会有这些字段。");
            return;
        }

        var sb = new System.Text.StringBuilder();
        void Line(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{label}：{value}");
        }

        Line("描述 (bext)", m.Description);
        Line("录制者", m.Originator);
        Line("录制者编号", m.OriginatorRef);
        Line("录制日期", (m.OriginationDate + " " + m.OriginationTime).Trim());
        Line("标题 (INAM)", m.InfoName);
        Line("作者 (IART)", m.InfoArtist);
        Line("备注 (ICMT)", m.InfoComment);
        Line("工程 (iXML)", m.IxmlProject);
        Line("场景 (iXML)", m.IxmlScene);
        Line("备注 (iXML)", m.IxmlNote);

        MessageDialog.Show(this, "元数据 · " + a.DisplayName, sb.ToString().TrimEnd());
    }
    private void CopySelection(bool toFiles)
    {
        var items = _selected.Count > 0
            ? _selected.ToList()
            : (_nowPlaying is not null ? new List<AssetItem> { _nowPlaying } : new List<AssetItem>());

        if (items.Count == 0)
        {
            ShowToast("先选中要复制的音效");
            return;
        }

        try
        {
            if (toFiles)
            {
                var files = new System.Collections.Specialized.StringCollection();
                foreach (var a in items) files.Add(a.FilePath);
                Clipboard.SetFileDropList(files);
                ShowToast(items.Count == 1 ? "已复制文件" : $"已复制 {items.Count} 个文件");
            }
            else
            {
                Clipboard.SetText(string.Join(Environment.NewLine, items.Select(a => a.FilePath)));
                ShowToast(items.Count == 1 ? "已复制路径" : $"已复制 {items.Count} 条路径");
            }
        }
        catch (Exception ex)
        {
            CrashLog("复制到剪贴板失败：" + ex.Message);
            ShowToast("复制失败");
        }
    }
    private void ShowPackInfo(string root)
    {
        if (Environment.GetEnvironmentVariable("SV_PACK_TEST") == "1")
        {
            var pack = PackScanner.Scan(root);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-pack.txt"),
                    $"name={pack.Name}\nversion={pack.Version}\ndisplay={pack.DisplayName}\n" +
                    $"cover={Path.GetFileName(pack.CoverPath)}\nlicenses={pack.LicenseFiles.Count}\n" +
                    $"incomplete={pack.IncompleteCount}\naudio={pack.AudioCount}\n", System.Text.Encoding.UTF8);
            }
            catch { }
            return;
        }

        var info = PackScanner.Scan(root);
        MessageDialog.Show(this, "素材包识别", info.Summary);
    }
    private async System.Threading.Tasks.Task ExportAsync()
    {
        var items = _selected.Count > 0 ? _selected.ToList() : _view.ToList();
        if (items.Count == 0)
        {
            MessageDialog.Show(this, "没有选中音效", "先在卡片上点选要导出的音效（按住 Ctrl / Shift 可多选）；直接点「导出」则导出当前列表的全部。");
            return;
        }

        var dlg = new OpenFolderDialog { Title = $"导出 {items.Count} 个音效到…" };
        if (dlg.ShowDialog() != true) return;

        int folders = items.Select(i => Path.GetDirectoryName(i.FilePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // 导出选项：命名模板 + 是否保留目录结构（跨文件夹时默认勾上）
        var options = ExportDialog.Ask(this, items.Count, items[0], _lib?.Name ?? "", folders > 1,
                                       string.IsNullOrWhiteSpace(_store.ExportTemplate) ? ExportNames.DefaultTemplate : _store.ExportTemplate);
        if (options is null) return;

        _store.ExportTemplate = options.Template;
        _store.Save();

        var progress = ProgressDialog.Open(this, $"正在导出 {items.Count} 个音效");
        progress.Report(0, items.Count, options.KeepStructure ? "保留目录结构" : "全部放同一层");

        try
        {
            var result = await ExportCoreAsync(dlg.FolderName, items, options.KeepStructure, options.Template, progress);
            progress.Close();

            if (result.Cancelled)
            {
                MessageDialog.Show(this, "已取消", $"已经导出 {result.Count} 个文件到：\n{dlg.FolderName}");
                return;
            }

            MessageDialog.Show(this, "导出完成",
                $"已导出 {result.Count} 个文件到：\n{dlg.FolderName}" +
                (options.KeepStructure && folders > 1 ? $"\n（按原来的文件夹分了 {folders} 个子目录）" : "") +
                $"\n\n清单：{result.CsvName}",
                "打开目录", () => RevealInExplorer(dlg.FolderName));
        }
        catch (Exception ex)
        {
            progress.Close();
            MessageDialog.Show(this, "导出失败", ex.Message);
        }
    }
    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }
    private async Task<(int Count, string CsvName, bool Cancelled)> ExportCoreAsync(
        string targetDir, List<AssetItem> items, bool keepStructure, string template, ProgressDialog progress)
    {
        var rows = new List<string>
        {
            "序号,文件名,显示名,库,相对路径,时长(秒),采样率,位深,声道,格式,大小(MB),评分,收藏"
        };

        string libRoot = _lib?.RootPath ?? "";
        int n = 0;
        bool cancelled = false;

        for (int i = 0; i < items.Count; i++)
        {
            if (progress.Cancelled) { cancelled = true; break; }

            var a = items[i];
            n++;

            var ext = Path.GetExtension(a.FilePath);
            var rel = libRoot.Length > 0 ? Path.GetRelativePath(libRoot, a.FilePath) : a.FileName;

            string destDir = targetDir;
            if (keepStructure)
            {
                var sub = Path.GetDirectoryName(rel);
                if (!string.IsNullOrEmpty(sub)) destDir = Path.Combine(targetDir, sub);
            }
            Directory.CreateDirectory(destDir);

            var dest = UniquePath(destDir, ExportNames.Expand(template, n, a, _lib?.Name ?? ""), ext);
            File.Copy(a.FilePath, dest, overwrite: false);

            var info = WavReader.TryRead(a.FilePath);
            rows.Add(string.Join(",",
                n.ToString(),
                Csv(Path.GetFileName(dest)),
                Csv(a.DisplayName),
                Csv(_lib?.Name ?? ""),
                Csv(rel),
                (a.DurationMs / 1000.0).ToString("0.00"),
                info?.SampleRate.ToString() ?? "",
                info?.Bits.ToString() ?? "",
                info?.Channels.ToString() ?? "",
                ext.TrimStart('.').ToUpperInvariant(),
                (new FileInfo(a.FilePath).Length / 1048576.0).ToString("0.00"),
                a.Rating.ToString(),
                a.IsFavorite ? "是" : ""));

            if (i % 3 == 0)
            {
                progress.Report(n, items.Count, a.DisplayName);
                await Dispatcher.Yield(DispatcherPriority.Background);   // 让进度窗能重画
            }
        }

        progress.Report(n, items.Count, cancelled ? "已取消" : "写入清单…");
        await Dispatcher.Yield(DispatcherPriority.Background);

        var csvName = $"音匣导出清单_{DateTime.Now:yyyyMMdd_HHmm}.csv";
        await File.WriteAllTextAsync(Path.Combine(targetDir, csvName), string.Join("\r\n", rows),
                                     new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return (n, csvName, cancelled);
    }
    private static string UniquePath(string dir, string baseName, string ext)
    {
        var name = baseName;
        var path = Path.Combine(dir, name + ext);
        int i = 1;
        while (File.Exists(path))
        {
            name = $"{baseName}_{i++}";
            path = Path.Combine(dir, name + ext);
        }
        return path;
    }
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
    private void TagPageRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 点是「改名 / 删除」按钮就别导航
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) is not null) return;

        if ((sender as FrameworkElement)?.DataContext is not TagRow row) return;
        e.Handled = true;

        OpenTag(row.Name);      // 不进搜索，走"库 › 标签 › 某标签"这一层
    }
    private void TagRename_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not TagRow row) return;

        var name = InputDialog.Ask(this, "标签改名",
            $"把「#{row.Name}」改成什么名字？如果新名字已经有标签，会合并成一个。", row.Name, "改好");
        if (name is null || name.Equals(row.Name, StringComparison.Ordinal)) return;

        try { _db?.RenameTag(row.Name, name); }
        catch (Exception ex) { CrashLog("标签改名失败：" + ex.Message); }

        RefreshTagPage();
        RefreshSmartFolders();
        RefreshCollections();
    }
    private void TagDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not TagRow row) return;

        _pendingTagDelete = row.Name;
        MessageDialog.Show(this, "删除标签",
            $"删掉「#{row.Name}」？这个标签会从 {row.Count} 个音效上摘掉，音效文件本身不受影响。",
            "删掉", () =>
            {
                try { _db?.DeleteTag(_pendingTagDelete ?? ""); }
                catch (Exception ex) { CrashLog("删标签失败：" + ex.Message); }
                _pendingTagDelete = null;
                RefreshTagPage();
                RefreshSmartFolders();
        RefreshCollections();
            });
    }
    private void ShowToast(string message)
    {
        if (!_toastHooked)
        {
            _toastHooked = true;
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
                Toast.BeginAnimation(OpacityProperty, fade);
            };
        }

        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        _toastTimer.Stop();
        _toastTimer.Start();
    }
    private (int Ok, List<string> Errors) ApplyRename(List<(AssetItem Item, string NewName)> plan)
    {
        var errors = new List<string>();
        int ok = 0;

        foreach (var (item, newBase) in plan)
        {
            try
            {
                var dir = Path.GetDirectoryName(item.FilePath) ?? "";
                var ext = Path.GetExtension(item.FilePath);
                var dst = Path.Combine(dir, newBase + ext);

                if (string.Equals(dst, item.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(dst)) continue;

                File.Move(item.FilePath, dst);
                _db?.RenamePath(item.FilePath, dst);
                _store.RenameAsset(item.FilePath, dst);
                item.FilePath = dst;
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.DisplayName}：{ex.Message}");
            }
        }
        return (ok, errors);
    }
    private void BatchRename()
    {
        var items = _selected.Count > 0 ? _selected.ToList() : _view.ToList();
        if (items.Count == 0)
        {
            ShowToast("先选中要改名的音效");
            return;
        }

        var dlg = BatchRenameDialog.Ask(this, items, _lib?.Name ?? "",
                                        string.IsNullOrWhiteSpace(_store.ExportTemplate) ? "{n:00}_{name}" : _store.ExportTemplate);
        if (dlg is null || dlg.Plan.Count == 0) return;

        var (ok, errors) = ApplyRename(dlg.Plan);
        _store.Save();
        ReloadCurrentView(_viewLibRoot);
        _perDirCache.Clear();

        if (errors.Count == 0)
            MessageDialog.Show(this, "重命名完成", $"已重命名 {ok} 个文件" + (dlg.Skipped > 0 ? $"，跳过 {dlg.Skipped} 个（同名/重名）" : "") + "。");
        else
            MessageDialog.Show(this, "重命名部分失败",
                $"成功 {ok} 个，失败 {errors.Count} 个：\n\n" + string.Join("\n", errors.Take(8)));
    }
}
