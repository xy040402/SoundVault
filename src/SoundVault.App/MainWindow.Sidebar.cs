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

/// <summary>左栏树与库管理（拆分出来的，逻辑未改）</summary>
public partial class MainWindow
{
    private LibraryItem? AddLibraryNode(string root)
    {
        // 防御：已经是某个已登记库的子目录 → 不要当新库登记（否则那些文件会从"全部音效"里消失）
        var owner = _libs.Keys
            .Where(k => root.StartsWith(k.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (owner is not null && _libs.TryGetValue(owner, out var ownerLib)) return ownerLib;

        root = Indexer.Normalize(root);
        if (_libs.TryGetValue(root, out var existing)) return existing;

        var lib = new LibraryItem { Name = _store.NameOf(root), RootPath = root };
        var perDir = FolderCountsOf(root);

        var rootNode = new FolderItem
        {
            Name = lib.Name,
            Path = root,
            IsLibrary = true,
            Depth = 0,
            IsExpanded = true
        };
        lib.Root = rootNode;
        BuildChildren(rootNode, perDir);
        rootNode.IsExpanded = true;
        rootNode.HasChildren = rootNode.Children.Count > 0;
        rootNode.FileCount = CountForFolder(perDir, root);

        lib.TotalFiles = rootNode.FileCount;
        _libs[root] = lib;

        // 侧栏 = 可见节点平铺（库 → 已展开的子文件夹…）
        RebuildSidebar();

        if (!_store.Libraries.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            _store.Libraries.Add(root);
            _store.Save();
        }
        return lib;
    }
    private async Task AddLibraryAsync(string root, bool select = true)
    {
        var log = Path.Combine(Path.GetTempPath(), "sv-import.txt");
        void L(string m) { try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n"); } catch { } }
        L($"enter root={root}");

        var pack = PackScanner.Scan(root);
        if (pack.Version.Length > 0 || pack.Name.Length > 0)
            _store.LibraryNames[Indexer.Normalize(root)] = pack.DisplayName;
        L($"pack scanned name={pack.Name} version={pack.Version} audio={pack.AudioCount}");

        var lib = AddLibraryNode(root);
        if (lib is null) { L("lib is null，返回"); return; }

        if (select) ShowLibrary(lib.RootPath);
        else if (_side.Count > 0 && FolderList.SelectedIndex < 0) SyncSidebarSelection(lib.Root);
        L($"树已建 显示已切 开启扫描…");

        await ScanLibrariesAsync(new[] { lib.RootPath });
        L($"ScanAsync 返回 lib.TotalFiles={lib.TotalFiles}");

        _perDirCache.Clear();
        RefreshLibraryCounts(lib.RootPath);
        L($"刷新完成 lib.TotalFiles={lib.TotalFiles} 子文件夹={lib.Root?.Children.Count}");

        if (string.Equals(_viewLibRoot, lib.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (select) ShowLibrary(lib.RootPath);
            else if (_viewKind == ViewKind.Folder && _currentFolder is not null) OpenFolder(_currentFolder);
            else ReloadCurrentView(lib.RootPath);
        }
        L($"视图已重画 统计={ContentStat.Text}");

        // 素材包识别放在最后：它以前挡在扫描前面，用户看到的是扫描前的空视图（v0.36 修）
        ShowPackInfo(root);
    }
    /// <summary>左栏四个列表互斥：选中一个就把其它三个的选中清掉，否则会出现两处高亮（v0.57）。</summary>
    private void ClearOtherSidebarLists(object? keep)
    {
        if (!ReferenceEquals(keep, QuickList))
        {
            bool o = _suppressQuick; _suppressQuick = true;
            try { QuickList.SelectedIndex = -1; } finally { _suppressQuick = o; }
        }
        if (!ReferenceEquals(keep, SmartList))
        {
            bool o = _suppressPickers; _suppressPickers = true;
            try { SmartList.SelectedIndex = -1; } finally { _suppressPickers = o; }
        }
        if (!ReferenceEquals(keep, CollectionList))
        {
            bool o = _suppressSidebar; _suppressSidebar = true;
            try { CollectionList.SelectedIndex = -1; } finally { _suppressSidebar = o; }
        }
        if (!ReferenceEquals(keep, FolderList))
        {
            bool o = _suppressSidebar; _suppressSidebar = true;
            try { FolderList.SelectedIndex = -1; } finally { _suppressSidebar = o; }
        }
    }

    private void ClearSidebarSelection()
    {
        bool old = _suppressSidebar;
        _suppressSidebar = true;
        try { FolderList.SelectedIndex = -1; }
        finally { _suppressSidebar = old; }
    }
    private void SyncSidebarSelection(FolderItem? node, bool scrollIntoView = true)
    {
        if (node is null) return;

        bool needRebuild = false;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (!p.IsExpanded) { p.IsExpanded = true; needRebuild = true; }
        }
        if (needRebuild) RebuildSidebarCore();

        int idx = _side.IndexOf(node);
        if (idx < 0) return;

        bool old = _suppressSidebar;
        _suppressSidebar = true;
        try
        {
            FolderList.SelectedIndex = idx;
            if (scrollIntoView) FolderList.ScrollIntoView(node);
        }
        finally { _suppressSidebar = old; }
    }
    private void RefreshSmartFolders()
    {
        if (_db is null) return;

        List<SmartFolder> list;
        try { list = _db.SavedQueries().Select(q => new SmartFolder(q.Id, q.Name, q.Query)).ToList(); }
        catch (Exception ex) { CrashLog("读智能文件夹失败：" + ex.Message); return; }

        SmartSection.Visibility = Visibility.Visible;
        SmartList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SmartEmptyHint.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        _suppressPickers = true;
        try
        {
            SmartList.ItemsSource = list;
            SmartList.SelectedIndex = -1;
        }
        finally { _suppressPickers = false; }
    }
    private void SmartList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickers) return;
        ClearOtherSidebarLists(SmartList);
        if (SmartList.SelectedItem is not SmartFolder f) return;

        bool same = string.Equals(SearchBox.Text, f.Query, StringComparison.Ordinal);

        _applyingSmart = true;
        try { SearchBox.Text = f.Query; }     // 点一下就回到这个查询
        finally { _applyingSmart = false; }

        // 文本框内容没变时 TextChanged 不会触发（重复点同一个智能文件夹就会"没反应"），这里补一次
        if (same || !_searchMode)
        {
            _searchMode = true;
            LoadSearchView();
            SaveSearchPill.Visibility = Visibility.Visible;
            UpdateSearchStat();
        }

        FocusKeyboard(true);
    }
    private void SmartList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (SmartList.SelectedItem is not SmartFolder f) return;
        e.Handled = true;

        var menu = new ContextMenu();
        var del = new MenuItem { Header = $"删除「{f.Name}」" };
        del.Click += (_, _) =>
        {
            try { _db?.DeleteQuery(f.Name); } catch { }
            RefreshSmartFolders();
        RefreshCollections();
        };
        menu.Items.Add(del);

        menu.PlacementTarget = SmartList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private void LibraryMenu_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FolderItem node || !node.IsLibrary) return;

        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "重命名库…" };
        rename.Click += (_, _) => RenameLibrary(node);
        menu.Items.Add(rename);

        var rescan = new MenuItem { Header = "重新扫描这个库" };
        rescan.Click += (_, _) => RescanLibrary(node);
        menu.Items.Add(rescan);

        var open = new MenuItem { Header = "在资源管理器中打开" };
        open.Click += (_, _) => RevealFolder(node.Path);
        menu.Items.Add(open);

        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "移除库…" };
        remove.Click += (_, _) => RemoveLibrary(node);
        menu.Items.Add(remove);

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private void FolderList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("SV_CRUMB_LOG") == "1")
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sv-libmenu.txt"), "right-click src=" + e.OriginalSource?.GetType().Name + "\n"); }
            catch { }
        }
        if (e.OriginalSource is not DependencyObject src) return;
        var row = FindAncestor<ListBoxItem>(src);
        if (row?.DataContext is not FolderItem node) return;

        e.Handled = true;
        var menu = new ContextMenu();

        if (node.IsLibrary)
        {
            var rename = new MenuItem { Header = "重命名库…" };
            rename.Click += (_, _) => RenameLibrary(node);
            menu.Items.Add(rename);

            var rescan = new MenuItem { Header = "重新扫描这个库" };
            rescan.Click += (_, _) => RescanLibrary(node);
            menu.Items.Add(rescan);

            menu.Items.Add(new Separator());
        }

        var open = new MenuItem { Header = "在资源管理器中打开" };
        open.Click += (_, _) => RevealFolder(node.Path);
        menu.Items.Add(open);

        var copy = new MenuItem { Header = "复制文件夹路径" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(node.Path); }
            catch (Exception ex) { CrashLog("复制文件夹路径失败：" + ex.Message); ShowToast("复制失败"); }
        };
        menu.Items.Add(copy);

        if (node.IsLibrary)
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "移除库…" };
            remove.Click += (_, _) => RemoveLibrary(node);
            menu.Items.Add(remove);
        }

        menu.PlacementTarget = row;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
    private void RenameLibrary(FolderItem node)
    {
        var current = _store.NameOf(node.Path);
        var name = InputDialog.Ask(this, "重命名库", "只改音匣里显示的名字，磁盘上的文件夹不受影响。", current, "改好");
        if (name is null || name == current) return;

        _store.LibraryNames[node.Path] = name;
        _store.Save();

        if (_libs.TryGetValue(node.Path, out var lib) && lib.Root is not null)
        {
            lib.Name = name;
            lib.Root.Name = name;
            if (_lib?.RootPath.Equals(node.Path, StringComparison.OrdinalIgnoreCase) == true)
                LibTab.Text = ShortLabel(name);
        }

        RebuildSidebar();

        if (_lib?.RootPath.Equals(node.Path, StringComparison.OrdinalIgnoreCase) == true)
            ShowLibrary(node.Path);      // 标题、面包屑跟着换
    }
    private void RescanLibrary(FolderItem node)
    {
        _ = ScanLibrariesAsync(new[] { node.Path });
    }
    private void RemoveLibrary(FolderItem node)
    {
        var name = _store.NameOf(node.Path);
        var tooltip = $"音匣里不再显示「{name}」以及它的索引（评分、标签也会一起清掉）。\n磁盘上的文件不会被删除。";

        MessageDialog.Show(this, "移除这个库", tooltip, "移除", () =>
        {
            _store.Libraries.RemoveAll(p => p.Equals(node.Path, StringComparison.OrdinalIgnoreCase));
            _store.LibraryNames.Remove(node.Path);
            _store.Save();

            try { _db?.DropLibrary(Indexer.Normalize(node.Path)); }
            catch (Exception ex) { CrashLog("移除库时清索引失败：" + ex.Message); }

            _libs.Remove(node.Path);
            if (_lib?.RootPath.Equals(node.Path, StringComparison.OrdinalIgnoreCase) == true) _lib = null;
            _perDirCache.Clear();

            RebuildSidebar();

            if (_libs.Count == 0) { ShowEmpty(); }
            else
            {
                var first = _libs.Values.First();
                if (first.Root is not null) ShowLibrary(first.RootPath);
            }
        });
    }
    private async void AddLibrary_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择一个素材包文件夹" };
        if (dlg.ShowDialog() == true) await AddLibraryAsync(dlg.FolderName);
    }
    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebar) return;                       // 程序性同步高亮时不要再触发导航
        ClearOtherSidebarLists(FolderList);
        if (FolderList.SelectedItem is not FolderItem f) return;
        FocusKeyboard(true);
        if (f.IsLibrary) ShowLibrary(f.Path);
        else OpenFolder(f);
    }
}
