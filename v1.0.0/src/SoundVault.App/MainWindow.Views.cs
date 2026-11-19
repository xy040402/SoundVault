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

/// <summary>视图与列表装配（拆分出来的，逻辑未改）</summary>
public partial class MainWindow
{
    private List<(string Label, FolderItem? Target, Action? Act)> TrailFor(FolderItem? node)
    {
        var trail = new List<(string, FolderItem?, Action?)>();
        var root = _lib?.Root;
        if (root is null) return trail;

        trail.Add((ShortLabel(root.Name, 20), root, null));

        if (node is null) return trail;      // 库根：就一段，且不可点

        var chain = new List<FolderItem>();
        for (var n = node; n is not null && !n.IsLibrary; n = n.Parent) chain.Insert(0, n);

        for (int i = 0; i < chain.Count; i++)
        {
            bool last = i == chain.Count - 1;
            trail.Add((chain[i].Name, last ? null : chain[i], null));
        }

        trail[0] = (ShortLabel(root.Name, 20), root, null);
        return trail;
    }
    private void ShowCrumbs(List<(string Label, FolderItem? Target, Action? Act)> parts)
    {
        CrumbBar.Children.Clear();

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                var sep = new TextBlock
                {
                    Text = "›",
                    Margin = new Thickness(1, 0, 1, 0),
                    Opacity = 0.55,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sep.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
                CrumbBar.Children.Add(sep);
            }

            bool last = i == parts.Count - 1;
            var (label, target, act) = parts[i];

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240
            };

            // 用 SetResourceReference 而不是 FindResource：这样换主题时会自动跟着变，
            // 不会留下一块"白字写在白底上"的残影（v0.18.1 修）
            text.SetResourceReference(TextBlock.ForegroundProperty, last ? "TextPrimary" : "TextSecondary");

            if (last || (target is null && act is null))
            {
                text.Margin = new Thickness(5, 2, 5, 2);
                CrumbBar.Children.Add(text);
                continue;
            }

            var box = new Border
            {
                Style = (Style)FindResource("CrumbBtn"),
                Child = text,
                ToolTip = target?.Path ?? label
            };
            var nav = target;
            var run = act;
            box.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (Environment.GetEnvironmentVariable("SV_CRUMB_LOG") == "1")
                {
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sv-crumb.txt"), "click: " + label + Environment.NewLine); }
                    catch { }
                }
                if (run is not null) run();
                else if (nav is not null) NavigateTo(nav);
            };
            CrumbBar.Children.Add(box);
        }
    }
    private void NavigateTo(FolderItem? node)
    {
        if (node is null) return;

        if (node.IsLibrary) { ShowLibrary(node.Path); return; }
        OpenFolder(node);
    }
    private void ShowLibrary(string root)
    {
        if (!_libs.TryGetValue(root, out var lib) || lib.Root is null) return;

        ClearSearch();
        _lib = lib;
        _viewLibRoot = root;
        _currentFolder = null;
        _viewKind = ViewKind.Library;
        ClearQuickSelection();          // 树里的"根"亮着，快捷入口就不该亮

        var children = lib.Root.Children;
        _subFolders.Clear();
        foreach (var f in children) _subFolders.Add(f);

        _all.Clear();
        _view.ResetWith(Array.Empty<AssetItem>());
        _selection.Clear();   // #51：模型也清，否则下次 Ctrl 点会带上旧视图的项
        _selected.Clear();
        _anchor = null;

        ApplyContentLayout(children.Count > 0, false);

        ContentTitle.Text = lib.Name;
        ContentStat.Text = $"{children.Count} 个文件夹 · {lib.TotalFiles} 个音效";
        LibTab.Text = ShortLabel(lib.Name);
        if (children.Count == 0) ShowFirstRunEmpty(); else HideEmptyViews();

        ShowCrumbs(TrailFor(null));      // 根目录：只有库名
        SyncSidebarSelection(lib.Root);
    }
    private void ApplyContentLayout(bool showFolders, bool showAssets, bool showTags = false)
    {
        // 标签页跨两行显示：两行高度必须有一行是 *，否则 RowSpan 的 ScrollViewer 高度是 0（v0.8 的坑）
        if (showTags)
        {
            FolderRowDef.Height = new GridLength(1, GridUnitType.Star);
            AssetRowDef.Height = new GridLength(0);
            FolderScroll.Visibility = Visibility.Collapsed;
            AssetList.Visibility = Visibility.Collapsed;
            TagPageScroll.Visibility = Visibility.Visible;
            return;
        }

        bool foldersFill = showFolders && !showAssets;

        FolderRowDef.Height = !showFolders ? new GridLength(0)
                            : foldersFill ? new GridLength(1, GridUnitType.Star)
                            : GridLength.Auto;
        AssetRowDef.Height = showAssets ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        TagPageScroll.Visibility = Visibility.Collapsed;
        FolderScroll.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
        FolderScroll.MaxHeight = foldersFill ? double.PositiveInfinity : 182;   // ≈ 两行卡片
        AssetList.Visibility = showAssets ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OpenFolder(FolderItem folder)
    {
        ClearSearch();
        _favoritesOnly = false;
        _currentFolder = folder;
        _viewKind = ViewKind.Folder;
        _viewLibRoot = LibraryOf(folder)?.RootPath ?? _lib?.RootPath ?? "";
        ClearQuickSelection();          // 进了文件夹，快捷入口的高亮要退掉

        EnsureChildren(folder);
        if (folder.HasChildren) folder.IsExpanded = true;   // 和资源管理器一样：进去就展开

        _subFolders.Clear();
        foreach (var c in folder.Children) _subFolders.Add(c);

        ContentTitle.Text = folder.Name;
        ShowCrumbs(TrailFor(folder));

        RebuildSidebarCore();       // 刚载入的子级 / 展开状态要反映到左栏
        SyncSidebarSelection(folder);

        List<FileRow> rows;
        try
        {
            rows = _db is null
                ? new List<FileRow>()
                : _deep ? _db.FilesUnderDir(folder.Path) : _db.FilesInDir(folder.Path);
        }
        catch (Exception ex)
        {
            CrashLog("从索引读文件夹失败：" + ex);
            rows = new List<FileRow>();
        }

        SetAssets(rows);

        bool hasFolders = folder.Children.Count > 0;
        ApplyContentLayout(hasFolders, _all.Count > 0);
        if (!hasFolders && _all.Count == 0)
            ShowEmptyView("这个文件夹里没有音频", "把工具栏的「深度」打开，可以把子文件夹里的音效也列出来。");
        else HideEmptyViews();
    }
    private void LoadAllView()
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.All;
        _favoritesOnly = false;
        _viewLibRoot = _lib.RootPath;
        _currentFolder = null;

        PrepareQuickView("全部音效");
        SetAssets(ReadAllRows());
    }
    private void LoadRecentView()
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.Recent;
        _favoritesOnly = false;
        _viewLibRoot = _lib.RootPath;
        _currentFolder = null;

        PrepareQuickView("最近导入");

        List<FileRow> rows;
        try { rows = _db?.FilesRecent(_lib.RootPath, 300) ?? new List<FileRow>(); }
        catch (Exception ex) { CrashLog("读取最近导入失败：" + ex); rows = new List<FileRow>(); }

        SetAssets(rows);
        ContentStat.Text = $"{_all.Count} 个音效 · 按导入时间倒序";
    }
    private void LoadPlayedView()
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.Recent;
        _favoritesOnly = false;
        _viewLibRoot = _lib.RootPath;
        _currentFolder = null;

        PrepareQuickView("最近播放");

        List<FileRow> rows;
        try { rows = _db?.FilesUnder(_lib.RootPath) ?? new List<FileRow>(); }
        catch (Exception ex) { CrashLog("读取最近播放失败：" + ex); rows = new List<FileRow>(); }

        var ordered = rows
            .Where(r => _store.PlayedAt.ContainsKey(r.Path))
            .OrderByDescending(r => _store.PlayedAt[r.Path])
            .Take(200)
            .ToList();

        SetAssets(ordered);
        ContentStat.Text = _all.Count == 0
            ? "还没有播放记录，悬停卡片试听一下就会记下"
            : $"{_all.Count} 个音效 · 按最近播放排序";
    }
    private void LoadFavoritesView()
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.Favorites;
        _viewLibRoot = _lib.RootPath;
        _currentFolder = null;

        PrepareQuickView("收藏");

        _favoritesOnly = true;                 // ApplyFilter 里按收藏过滤
        SetAssets(ReadAllRows());
        ContentStat.Text = $"{_view.Count} 个收藏";
        if (_view.Count == 0)
            ShowEmptyView("还没有收藏", "点卡片右上角的爱心即可收藏，收藏过的声音都会集中在这里。", "\uE734");
        else HideEmptyViews();
    }
    private void PrepareQuickView(string title)
    {
        ContentTitle.Text = title;
        ApplyContentLayout(false, true);

        var trail = TrailFor(null);
        trail.Add((title, null, null));
        ShowCrumbs(trail);

        ClearSidebarSelection();               // 快捷入口自己亮就够了，树里不再选任何节点
    }
    private void SetAssets(List<FileRow> rows)
    {
        _all.Clear();
        _selection.Clear();   // #51：模型也清，否则下次 Ctrl 点会带上旧视图的项
        _selected.Clear();
        _anchor = null;

        // 标签一次性批量查，别一张卡查一次
        Dictionary<string, List<string>> tags = new(StringComparer.OrdinalIgnoreCase);
        if (_db is not null)
        {
            try { tags = _db.TagsByPath(rows.Select(r => r.Path)); }
            catch (Exception ex) { CrashLog("读取标签失败：" + ex.Message); }
        }

        foreach (var r in rows)
        {
            var item = CreateAsset(r);
            if (tags.TryGetValue(r.Path, out var list)) item.SetTags(list);
            _all.Add(item);
        }

        ApplyFilter();
        int missing = _all.Count(x => x.IsMissing);
        ContentStat.Text = missing > 0 ? $"{_all.Count} 个音效 · {missing} 个丢失" : $"{_all.Count} 个音效";

        AssetScrollViewer?.ScrollToTop();
    }
    private AssetItem CreateAsset(FileRow r)
    {
        _store.Assets.TryGetValue(r.Path, out var ua);

        string meta;
        if (!r.Ok) meta = "无法解析";
        else
        {
            string sr = r.SampleRate % 1000 == 0 ? $"{r.SampleRate / 1000}k" : $"{r.SampleRate / 1000.0:0.#}k";
            meta = $"{r.DurationMs / 1000.0:0.00}s · {sr} · {r.Bits}bit · {r.Channels}ch";
        }

        return new AssetItem
        {
            Index = ParseIndex(r.Path),
            DisplayName = ParseDisplayName(r.Path),
            UsedToday = _store.UsedToday(r.Path),
            FileName = Path.GetFileName(r.Path),
            FilePath = r.Path,
            DurationMs = r.DurationMs,
            SampleRate = r.SampleRate,
            Channels = r.Channels,
            Bits = r.Bits,
            Extension = Path.GetExtension(r.Path).ToLowerInvariant(),
            IsMissing = r.Missing,
            Meta = meta,
            Rating = ua?.Rating ?? 0,
            IsFavorite = ua?.Favorite ?? false,
            PeaksBytes = r.Peaks
        };
    }
    private void ShowEmpty()
    {
        // 删库后必须把旧内容清干净：否则文件夹卡片会残留在右边，和空状态叠在一起（v0.36 修）
        _subFolders.Clear();
        _view.ResetWith(Array.Empty<AssetItem>());
        _all.Clear();
        _selection.Clear();   // #51：模型也清，否则下次 Ctrl 点会带上旧视图的项
        _selected.Clear();
        _anchor = null;
        _currentFolder = null;

        ApplyContentLayout(true, false);
        ShowFirstRunEmpty();
        ContentTitle.Text = "音效";
        ContentStat.Text = "";
        CrumbBar.Children.Clear();
    }
    private void ShowEmptyView(string title, string body, string glyph = "\uE8B7")
    {
        EmptyViewIcon.Text = glyph;
        EmptyViewTitle.Text = title;
        EmptyViewBody.Text = body;
        EmptyView.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
    }
    private void ReloadCurrentView(string root)
    {
        if (!string.Equals(_viewLibRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        if (_searchMode) { LoadSearchView(); UpdateSearchStat(); return; }

        switch (_viewKind)
        {
            case ViewKind.Folder when _currentFolder is not null: OpenFolder(_currentFolder); break;
            case ViewKind.All: LoadAllView(); break;
            case ViewKind.Recent: LoadRecentView(); break;
            case ViewKind.Favorites: LoadFavoritesView(); break;
            case ViewKind.TagDetail when _currentTag is not null: OpenTag(_currentTag); break;
            case ViewKind.Tags: LoadTagsView(); break;
            default: if (_lib is not null) ShowLibrary(_lib.RootPath); break;
        }
    }
    private void LoadCollectionView(Collection col)
    {
        ClearSearch();
        _viewKind = ViewKind.Collection;
        _currentCollection = col;          // 记住当前集合（右键「从集合中移除」要用）
        _favoritesOnly = false;
        _currentFolder = null;
        _viewLibRoot = _lib?.RootPath ?? "";

        if (_lib?.Root is null) { ShowEmpty(); return; }

        ContentTitle.Text = col.Name;
        ApplyContentLayout(showFolders: false, showAssets: true);

        var trail = TrailFor(null);
        trail.Add(("集合", null, null));
        trail.Add((col.Name, null, null));
        ShowCrumbs(trail);

        List<FileRow> rows;
        try { rows = _db?.FilesUnder(_lib.RootPath) ?? new List<FileRow>(); }
        catch (Exception ex) { CrashLog("读取集合失败：" + ex); rows = new List<FileRow>(); }

        var set = new HashSet<string>(col.Paths, StringComparer.OrdinalIgnoreCase);
        SetAssets(rows.Where(r => set.Contains(r.Path)).ToList());

        ContentStat.Text = _all.Count == 0
            ? "这个集合还是空的 · 选中音效后右键「添加到集合」"
            : $"{_all.Count} 个音效 · 手动集合";
    }
    private void LoadSearchView()
    {
        if (_lib?.Root is null) return;

        ContentTitle.Text = "搜索";
        ApplyContentLayout(false, true);

        var trail = TrailFor(null);
        trail.Add(("搜索", null, null));
        ShowCrumbs(trail);

        SetAssets(ReadAllRows());       // 全库；SetAssets 内部会 ApplyFilter
    }
    private void UpdateSearchStat()
    {
        if (!_searchMode) return;
        ContentStat.Text = $"{_view.Count} 个结果 · “{SearchBox.Text.Trim()}”";
    }
    private void ApplyFilter()
    {
        if (_view is null) return;

        // #51：过滤/排序逻辑搬进 SearchViewModel（可单测），这里只负责取数据和塞列表
        _search.FavoritesOnly = _favoritesOnly;
        _search.SortMode = _sortMode;
        _search.SortDescending = _sortDesc;
        _search.SetText(SearchBox.Text);

        _view.ResetWith(_search.Apply(_all));

        // 有内容却一条都没筛出来 → 告诉用户是"搜索没命中"，而不是让他盯着空屏
        if (_view.Count == 0 && _all.Count > 0)
            ShowEmptyView("没有匹配的音效", "换个关键词试试；把搜索框里的字删掉就能看到全部。\n也支持过滤语法：#标签、star:>=4、dur:<2s、sr:>48k、ch:2、type:wav（点搜索框里的「?」看说明）。", "\uE721");
        else if (_view.Count > 0)
            HideEmptyViews();
    }
    private void LoadTagsView()
    {
        ClearSearch();
        _viewKind = ViewKind.Tags;
        _favoritesOnly = false;
        _currentFolder = null;

        ContentTitle.Text = "标签";
        _viewLibRoot = _lib?.RootPath ?? "";
        ApplyContentLayout(false, false, showTags: true);

        var trail = TrailFor(null);
        trail.Add(("标签", null, null));
        ShowCrumbs(trail);

        ClearSidebarSelection();
        RefreshTagPage();
    }
    private void LoadDuplicatesView()
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.Duplicates;
        _favoritesOnly = false;
        _currentFolder = null;
        _viewLibRoot = _lib.RootPath;

        ContentTitle.Text = "重复检查";
        ApplyContentLayout(false, false, showTags: true);     // 复用标签页那个列表容器

        var trail = TrailFor(null);
        trail.Add(("重复检查", null, null));
        ShowCrumbs(trail);

        ClearSidebarSelection();

        List<List<FileRow>> groups;
        try { groups = _db?.DuplicateGroups(_lib.RootPath) ?? new(); }
        catch (Exception ex) { CrashLog("查重复失败：" + ex.Message); groups = new(); }

        var rows = groups.Select(g =>
        {
            var first = g[0];
            var folders = g.Select(f => f.Dir).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string where = folders.Count == 1
                ? Path.GetFileName(folders[0])
                : string.Join(" / ", folders.Take(2).Select(Path.GetFileName)) + (folders.Count > 2 ? $" 等 {folders.Count} 个文件夹" : "");
            return new DuplicateRow(
                $"{first.DurationMs / 1000.0:0.00}s · {first.Size / 1024.0 / 1024.0:0.0} MB",
                g.Count,
                where,
                string.Join("\n", g.Select(f => f.Path)));
        }).ToList();

        TagPageList.ItemTemplate = (DataTemplate)FindResource("DupRowTpl");
        TagPageList.ItemsSource = rows;

        int total = rows.Sum(r => r.Count - 1);
        ContentStat.Text = rows.Count == 0 ? "没有发现重复" : $"{rows.Count} 组 · 可省下 {total} 个";

        if (rows.Count == 0)
            ShowEmptyView("没有发现重复", "同一个库里「文件大小 + 时长」都相同的音效会被列在这里。", "\uE8B7");
        else HideEmptyViews();
    }
}
