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

public partial class MainWindow : Window
{

    private readonly ObservableCollection<FolderItem> _side = new();
    private readonly ObservableCollection<FolderItem> _subFolders = new();
    private readonly RangeObservableCollection<AssetItem> _view = new();
    private readonly List<AssetItem> _all = new();
    private readonly HashSet<AssetItem> _selected = new();
    private readonly Dictionary<string, LibraryItem> _libs = new(StringComparer.OrdinalIgnoreCase);

    private readonly AudioEngine _audio = new();
    private readonly Store _store = Store.Load();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _scanPillTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _frameHooked;
    private string _lastTime = "";
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly System.Diagnostics.Stopwatch BootClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrameMs;

    private IndexDb? _db;
    private Indexer? _indexer;
    private CancellationTokenSource? _scanCts;
    private bool _scanRunning;
    private string _viewLibRoot = "";

    private LibraryItem? _lib;
    private AssetItem? _hoverItem;
    private AssetItem? _pinned;
    private AssetItem? _nowPlaying;
    private Collection? _currentCollection;   // 当前正在看的集合（用于「从集合中移除」）
    private AssetItem? _anchor;
    private AssetItem? _dragItem;
    private Point _pressPoint;
    private bool _canDrag;
    private AssetItem? _scrubbing;                 // 正在拖动定位的卡片
    private FrameworkElement? _scrubTarget;        // 拖动的那个波形元素（要释放鼠标捕获）
    private double _lastScrubMs = -1000;
    private readonly SoundVault.App.ViewModels.SearchViewModel _search = new();   // #51 搜索/筛选模型
    private readonly SoundVault.App.ViewModels.SelectionViewModel _selection = new(); // #51 选中集模型
    private bool _isDark = true;
    private bool _scanned;
    private bool _favoritesOnly;
    private bool _deep;
    private bool _volDragging;
    private int _sortMode;              // 0 名称 / 1 时长 / 2 评分 / 3 导入顺序
    private bool _sortDesc;             // false = ↑ 升序，true = ↓ 降序
    private FolderItem? _currentFolder;
    private ViewKind _viewKind = ViewKind.Library;
    private enum ViewKind { Library, All, Recent, Favorites, Folder, Tags, TagDetail, Duplicates, Collection }
    private bool _suppressQuick;            // 程序性改快捷入口选中时，别触发加载
    private bool _suppressSidebar;          // 程序性改选中时，别触发导航
    private bool _searchMode;               // 正在显示"整库搜索结果"
    private bool _suppressSearch;           // 程序性改搜索框时，别触发重载
    private readonly Dictionary<string, Dictionary<string, int>> _perDirCache = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();

        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1280, Math.Max(1040, wa.Width * 0.92));
        Height = Math.Min(800, Math.Max(640, wa.Height * 0.92));

        FolderList.ItemsSource = _side;
        SubFolderList.ItemsSource = _subFolders;
        AssetList.ItemsSource = _view;

        _hoverTimer.Tick += HoverTick;
        _scanPillTimer.Tick += OnScanPillTimer;

        _audio.PlaybackEnded += () => Dispatcher.Invoke(() =>
        {
            _pinned = null;
            SyncPinFlags();
            SetPlaying(null);
            UpdatePlayIcon(false);
        });

        Activated += (_, _) => FocusKeyboard();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { _scanCts?.Cancel(); } catch { }
            _audio.Dispose();
            _store.Save();
            _db?.Dispose();
        };

        UpdateSortLabel();
    }

    // ── 启动 ────────────────────────────────────────────────
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scanned) return;
        _scanned = true;

        var env = Environment.GetEnvironmentVariable("SV_THEME");
        _isDark = env is null
            ? !string.Equals(_store.Theme, "light", StringComparison.OrdinalIgnoreCase)
            : !string.Equals(env, "light", StringComparison.OrdinalIgnoreCase);
        ApplyTheme(_isDark);

        ApplyAccentFromStore();      // 启动时套上保存的主色与面板强度
        Topmost = _store.Topmost;    // 窗口置顶（#43）
        SyncPinButton();


        if (_store.SidebarWidth >= 150 && _store.SidebarWidth <= 560)
            SidebarColumn.Width = new GridLength(_store.SidebarWidth);

        if (FpsProbe) CompositionTarget.Rendering += ProbeFps;
        HookPointerGlow();

        var roots = _store.Libraries.Select(TryFullPath).Where(Directory.Exists).ToList();

        // 恢复音量 / 归一化 / 输出设备
        _audio.Volume = (float)Math.Clamp(_store.Volume, 0, 1);
        _audio.Normalize = _store.Normalize;
        if (!string.IsNullOrEmpty(_store.DeviceId))
        {
            var devices = AudioEngine.EnumerateDevices();
            var hit = devices.FirstOrDefault(d => string.Equals(d.Id, _store.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (hit.Id is not null)
            {
                _audio.SetDevice(hit.Id);
                DeviceText.Text = ShortDevice(hit.Name);
            }
        }
        SyncTogglePills();
        UpdateVolumeUi();

        // 打开索引库；万一打不开就退回旧的"每次现场枚举"，不能拦住启动
        try
        {
            _db = IndexDb.Open(Environment.GetEnvironmentVariable("SV_DB"));
            _indexer = new Indexer(_db);
        }
        catch (Exception ex)
        {
            CrashLog("索引库打开失败，退回现场扫描：\n" + ex);
            _db = null;
            _indexer = null;
        }

        // 先用索引里的结果把界面画出来（不碰文件系统，万级库也是毫秒级）

        // 修一次历史数据：把"lib 被记成子文件夹"的行归回库根（否则那些文件会从全部音效里消失）
        try
        {
            int repaired = _db?.RepairNestedLibraries() ?? 0;
            if (repaired > 0) CrashLog($"修好 {repaired} 条库归属错误的索引记录");
        }
        catch (Exception ex) { CrashLog("修复库归属失败：" + ex.Message); }

        InitOnboarding();          // 首次启动弹新手引导（store.Onboarded=false 时）
        MaybeStartOnboarding();
        RefreshSmartFolders();
        RefreshCollections();

        foreach (var root in roots) AddLibraryNode(root);

        if (_side.Count > 0) FolderList.SelectedIndex = 0;
        else ShowEmpty();

        FocusKeyboard(true);        // 启动时还没有人在搜索，强制把焦点收回到窗口
        LogStartup("首屏可交互");

        // 云母要在 HWND 建好之后开；SV_MICA 可临时压过设置
        bool micaOn = _store.Mica;
        var micaEnv = Environment.GetEnvironmentVariable("SV_MICA");
        if (micaEnv is not null) micaOn = micaEnv == "1";
        if (micaOn) ApplyMica(true);

        // 然后后台增量扫描：没变化的文件直接跳过，只补新增/改动的
        if (roots.Count > 0) await ScanLibrariesAsync(roots);

        await RunExportTestIfRequestedAsync();
        await RunRenameTestIfRequestedAsync();
        RunMetaTestIfRequested();
        RunPackTestIfRequested();
        RunLufsTestIfRequested();

        // 右键「添加到音匣」带来的文件（#49）
        if (!string.IsNullOrEmpty(PendingAdd))
        {
            var pending = PendingAdd;
            PendingAdd = null;
            _ = AddExternalFileAsync(pending);
        }

        await RunListTestIfRequestedAsync();
        await RunOrgTestIfRequestedAsync();
        DumpDbIfRequested();
        await ThemeDialogTestIfRequestedAsync();

        LogStartup("启动流程结束");

        // 视觉验收用：SV_DIALOG_TEST=1 时弹出提示框样例
        if (Environment.GetEnvironmentVariable("SV_DIALOG_TEST") == "1")
        {
            await Task.Delay(700);
            MessageDialog.Show(this, "导出完成",
                "已导出 13 个文件到：\nC:\\Users\\22846\\Desktop\\武侠短片\\音频\\SFX\n\n清单：音匣导出清单_20261031_0334.csv",
                "打开目录", () => { });
        }
    }

    private static string TryFullPath(string p)
    {
        try { return Indexer.Normalize(p); }
        catch { return p; }
    }

    /// <summary>启动耗时探针：SV_STARTUP=1 时把关键节点写进 %TEMP%\sv-start.txt。</summary>
    private static readonly bool StartupProbe = Environment.GetEnvironmentVariable("SV_STARTUP") == "1";

    private static void LogStartup(string stage)
    {
        if (!StartupProbe) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "sv-start.txt"),
                $"{stage}: {BootClock.Elapsed.TotalMilliseconds:0} ms\n", System.Text.Encoding.UTF8);
        }
        catch { }
    }

    internal static void CrashLog(string text)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sv-crash.txt"), text + "\n", System.Text.Encoding.UTF8); }
        catch { }
    }

    // 供自动化验收：SV_LUFS_TEST=<文件夹> 时把里面 wav 的 LUFS / 增益写日志
    // 供自动化验收：SV_PACK_TEST=<文件夹> 时识别素材包并写日志
    // 供自动化验收：SV_META_TEST=<wav> 时解析元数据并写日志
    // 供自动化验收：SV_RENAME_TEST=1 时把当前文件夹前 3 个改名，写日志（配合临时库使用）
    // 供自动化验收：SV_EXPORT_TEST=<目录> 时直接导出当前文件夹并写日志

    // ── 素材包 ──────────────────────────────────────────────
    /// <summary>
    /// 把一个库挂到侧栏：文件夹列表现场枚举（只有一层，很快），数字全部来自索引库。
    /// 所以启动时不需要遍历素材，万级库也是毫秒级。
    /// </summary>

    /// <summary>枚举某个节点的直接子文件夹（懒加载，只在需要时扫一层）。</summary>
    private void BuildChildren(FolderItem node, Dictionary<string, int> perDir)
    {
        if (node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;

        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(node.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { dirs = new List<string>(); }

        int i = 0;
        foreach (var d in dirs)
        {
            var child = new FolderItem
            {
                Name = Path.GetFileName(d),
                Path = d,
                Parent = node,
                Depth = node.Depth + 1,
                FileCount = CountForFolder(perDir, d),
                ThumbBrush = node.IsLibrary ? MakeThumb(i) : node.ThumbBrush
            };

            // 有没有下一层（只探一层，决定左栏要不要画三角）
            try { child.HasChildren = Directory.EnumerateDirectories(d).Any(); }
            catch { child.HasChildren = false; }

            node.Children.Add(child);
            i++;
        }
    }


    /// <summary>用索引刷新计数（扫描完成后调用；原地更新，不重建树）。</summary>
    private void RefreshLibraryCounts(string root)
    {
        if (!_libs.TryGetValue(root, out var lib)) return;

        _perDirCache.Remove(root);           // 扫描后计数变了，缓存作废
        var perDir = FolderCountsOf(root);
        RefreshCountsRecursive(lib.Root, perDir);
        if (lib.Root is not null)
            lib.Root.FileCount = CountForFolder(perDir, lib.Root.Path);   // 库节点自己的总数
        lib.TotalFiles = lib.Root?.FileCount ?? 0;

        // 子文件夹列表里的卡片与树上是同一批对象，计数会自动跟着变；这里只补标题统计
        if (ReferenceEquals(_lib, lib) && _currentFolder is null)
            ContentStat.Text = $"{lib.Root?.Children.Count ?? 0} 个文件夹 · {lib.TotalFiles} 个音效";
    }

    private static void RefreshCountsRecursive(FolderItem? node, Dictionary<string, int> perDir)
    {
        if (node is null) return;
        foreach (var child in node.Children)
        {
            child.FileCount = CountForFolder(perDir, child.Path);
            RefreshCountsRecursive(child, perDir);
        }
    }

    /// <summary>
    /// "目录 → 该目录及其所有子目录里的文件数"（按库缓存，展开树时不反复查库）。
    /// 一次性把每个目录的计数沿父链向上累加，查的时候就是 O(1)。
    /// </summary>
    private Dictionary<string, int> FolderCountsOf(string root)
    {
        if (_perDirCache.TryGetValue(root, out var cached)) return cached;

        var raw = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_db is not null)
        {
            try
            {
                foreach (var (dir, n) in _db.FolderCounts(root)) raw[dir] = n;
            }
            catch (Exception ex) { CrashLog("读取索引计数失败：" + ex.Message); }
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, n) in raw)
        {
            var cur = dir;
            while (!string.IsNullOrEmpty(cur))
            {
                map.TryGetValue(cur, out int v);
                map[cur] = v + n;

                int cut = cur.LastIndexOf('\\');
                if (cut <= 2) break;          // 到盘符就停
                cur = cur[..cut];
            }
        }

        _perDirCache[root] = map;
        return map;
    }

    /// <summary>某个文件夹下的文件数（已经按父链累加过，含所有子目录）。</summary>
    private static int CountForFolder(Dictionary<string, int> perDir, string folder) =>
        perDir.TryGetValue(folder, out int n) ? n : 0;

    private void SelectLibraryNode(string root) =>
        SyncSidebarSelection(_libs.TryGetValue(root, out var l) ? l.Root : null);

    // ── 侧栏树（展开 / 收起 / 选中同步）──────────────────────
    /// <summary>音效网格内部的 ScrollViewer（ListBox 模板里的那个）。</summary>
    private ScrollViewer? AssetScrollViewer => FindDescendant<ScrollViewer>(AssetList);

    /// <summary>重建侧栏并把选中落到当前视图对应的节点上。</summary>
    private void RebuildSidebar()
    {
        RebuildSidebarCore();
        SyncSidebarSelection(_currentFolder ?? _lib?.Root);
    }

    /// <summary>把"可见节点"平铺进侧栏：库节点 + 所有已展开节点的子级。</summary>
    private void RebuildSidebarCore()
    {
        bool old = _suppressSidebar;
        _suppressSidebar = true;
        try
        {
            _side.Clear();
            foreach (var lib in _libs.Values)
                if (lib.Root is not null) AddVisibleNodes(lib.Root);
        }
        finally { _suppressSidebar = old; }
    }

    private void AddVisibleNodes(FolderItem node)
    {
        _side.Add(node);
        if (!node.IsExpanded) return;
        foreach (var child in node.Children) AddVisibleNodes(child);
    }

    /// <summary>清掉左栏树的选中（快捷入口视图用：同一时间只允许一处"亮"）。</summary>

    /// <summary>清掉快捷入口的选中（进到某个文件夹/库根时用）。</summary>
    private void ClearQuickSelection()
    {
        bool old = _suppressQuick;
        _suppressQuick = true;
        try { QuickList.SelectedIndex = -1; }
        finally { _suppressQuick = old; }
    }

    /// <summary>让侧栏选中某个节点；祖先没展开就先展开（这正是"右边点进去，左边跟着亮起来"）。</summary>

    /// <summary>点侧栏的三角：只折叠/展开，不切换右边视图。</summary>
    private void Chevron_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;       // 别让 ListBoxItem 把这下当成"选中 → 打开文件夹"
        if ((sender as FrameworkElement)?.DataContext is not FolderItem f) return;
        FocusKeyboard(true);
        if (!f.HasChildren) return;

        EnsureChildren(f);
        f.IsExpanded = !f.IsExpanded;

        RebuildSidebarCore();

        // 折叠后当前节点可能被藏起来 —— 那就选中它最近的可见祖先
        var visible = _currentFolder ?? _lib?.Root;
        while (visible is not null && _side.IndexOf(visible) < 0) visible = visible.Parent;
        SyncSidebarSelection(visible ?? _lib?.Root, scrollIntoView: false);
    }

    private void EnsureChildren(FolderItem node)
    {
        if (node.ChildrenLoaded) return;
        var lib = LibraryOf(node);
        BuildChildren(node, lib is null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : FolderCountsOf(lib.RootPath));
    }

    private LibraryItem? LibraryOf(FolderItem node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.IsLibrary && _libs.TryGetValue(n.Path, out var lib)) return lib;
        return null;
    }

    // ── 面包屑 ──────────────────────────────────────────────
    /// <summary>
    /// 根目录只显示库名（不追"全部"）；进了子文件夹才逐级追加，每段点一下回到那一层。
    /// </summary>



    // ── 视图 ────────────────────────────────────────────────

    /// <summary>内容区排布：上面文件夹卡片条（最多两行），下面音效网格，标签页独占。</summary>

    private static readonly uint[][] ThumbPalette =
    {
        new uint[] { 0xFF38BDF8, 0xFF1D4ED8 },
        new uint[] { 0xFFA78BFA, 0xFF6D28D9 },
        new uint[] { 0xFFFBBF24, 0xFFB45309 },
        new uint[] { 0xFF34D399, 0xFF047857 },
        new uint[] { 0xFFF472B6, 0xFFBE185D },
        new uint[] { 0xFF60A5FA, 0xFF1E40AF }
    };

    private static System.Windows.Media.Brush MakeThumb(int i)
    {
        var p = ThumbPalette[i % ThumbPalette.Length];
        var br = new System.Windows.Media.LinearGradientBrush(ToColor(p[0]), ToColor(p[1]), 45);
        br.Freeze();
        return br;
    }

    private static System.Windows.Media.Color ToColor(uint v) =>
        System.Windows.Media.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>
    /// 打开一个文件夹：左边选中它（必要时层层展开），上面显示它的子文件夹，下面显示它的音效。
    /// 这样从右边一层层点下去，左边会一路跟着展开并高亮。
    /// </summary>

    // ── 快捷入口的三种视图（都不选中左栏，避免两处同时亮）──────────


    /// <summary>最近播放（#23）：按 store.json 里记的播放时间倒序。</summary>


    /// <summary>快捷入口视图的公共部分：标题、面包屑、布局，并清掉左栏选中。</summary>

    private List<FileRow> ReadAllRows()
    {
        try { return _db?.FilesUnder(_lib?.RootPath ?? "") ?? new List<FileRow>(); }
        catch (Exception ex)
        {
            CrashLog("从索引读全库失败：" + ex);
            return new List<FileRow>();
        }
    }

    /// <summary>把索引里的行变成界面卡片。波形来自缓存的峰值，不再读文件。</summary>



    /// <summary>第一次用：一个库都还没有。</summary>
    private void ShowFirstRunEmpty()
    {
        EmptyState.Visibility = Visibility.Visible;
        EmptyView.Visibility = Visibility.Collapsed;
    }

    /// <summary>当前视图没东西（收藏为空 / 文件夹没音频 / 库是空的）。</summary>

    private void HideEmptyViews()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 验收钩子：SV_LIST_TEST=1 时自动加载"全部音效"并逐屏滚动，
    /// 记录每一步的布局耗时和实际创建的卡片数 —— 用来证明虚拟化真的生效（不依赖前台/鼠标）。
    /// </summary>

    /// <summary>往上找某类祖先（判断点的到底是按钮还是行）。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T hit) return hit;
            var deeper = FindDescendant<T>(c);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    // ── 增量扫描 + 进度 ─────────────────────────────────────
    private async Task ScanLibrariesAsync(IEnumerable<string> roots)
    {
        if (_indexer is null) return;

        _scanRunning = true;
        _scanCts = new CancellationTokenSource();
        _scanPillTimer.Start();          // 0.35 秒后才显示，快速扫描不会闪一下
        try
        {
            foreach (var root in roots)
            {
                if (_db is null) break;

                ScanResult result;
                try
                {
                    var progress = new Progress<ScanProgress>(UpdateScanUi);
                    result = await _indexer.ScanAsync(root, progress, _scanCts.Token);
                }
                catch (Exception ex)
                {
                    CrashLog("扫描失败：" + root + "\n" + ex);
                    continue;
                }

                RefreshLibraryCounts(root);

                int change = result.Added + result.Changed + result.Removed;
                LogStartup($"扫描 {Path.GetFileName(root)}：共 {result.Total} 个 / 新增 {result.Added} / 变更 {result.Changed} / 移除 {result.Removed} / {result.Elapsed.TotalMilliseconds:0} ms");

                // 索引没变化就不重画界面：第二次启动完全不会闪
                if (change > 0) ReloadCurrentView(root);
            }
        }
        finally
        {
            _scanRunning = false;
            EndScanPill();
        }
    }


    private void OnScanPillTimer(object? sender, EventArgs e)
    {
        _scanPillTimer.Stop();
        if (_scanRunning) ShowScanPill("正在建立索引…");
    }

    private void ShowScanPill(string text)
    {
        ScanText.Text = text;
        ScanScale.ScaleX = 0;
        ScanPill.Visibility = Visibility.Visible;
    }

    private void UpdateScanUi(ScanProgress p)
    {
        if (ScanPill.Visibility != Visibility.Visible) return;

        ScanScale.ScaleX = p.Total > 0 ? p.Fraction : 0;

        string text = p.Phase;
        if (p.Total > 0) text += $"  {p.Done:N0} / {p.Total:N0}";
        ScanText.Text = text;
    }

    private void EndScanPill()
    {
        _scanPillTimer.Stop();
        ScanPill.Visibility = Visibility.Collapsed;
    }

    // ── 键盘焦点 ────────────────────────────────────────────
    /// <summary>
    /// 把键盘焦点收回到窗口本身。
    /// WPF 激活窗口时会把焦点给第一个可输入/可聚焦的元素（这里是搜索框），
    /// 那样空格、F、1-5 就变成往搜索框里打字，用户会觉得"快捷键没反应"。
    /// </summary>
    private void FocusKeyboard(bool force = false)
    {
        if (!force && Keyboard.FocusedElement is TextBox) return;   // 启动时用户可能正想搜索
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>
    /// 验收钩子：SV_ORG_TEST=1 时自动跑一遍"标签 → 过滤语法 → 智能文件夹"，
    /// 结果写 %TEMP%\sv-org.txt。配合 SV_DB 指向临时索引库，不动用户数据。
    /// </summary>
    /// <summary>SV_DB_DUMP=1：把索引库里"哪些文件被标成丢失"写出来，排查用。</summary>
    /// <summary>点卡片右上角的爱心：直接收藏/取消收藏（不用记快捷键）。</summary>
    private void Heart_Click(object sender, RoutedEventArgs e)
    {
        _canDrag = false;
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;
        ToggleFavorite(a);
        FocusKeyboard();
    }

    private void ToggleFavorite(AssetItem a)
    {
        a.IsFavorite = !a.IsFavorite;
        _store.For(a.FilePath).Favorite = a.IsFavorite;
        _store.Save();

        ShowToast(a.IsFavorite ? "已收藏：" + a.DisplayName : "已取消收藏");

        if (_favoritesOnly) ApplyFilter();      // 在收藏页里取消收藏，卡片应该立刻消失
        else if (_searchMode) ApplyFilter();
    }

    private void SetRating(AssetItem a, int stars)
    {
        a.Rating = Math.Clamp(stars, 0, 5);
        _store.For(a.FilePath).Rating = a.Rating;
        _store.Save();
    }

    /// <summary>给卡片加/去一个标签，然后刷新左栏与当前视图。</summary>

    /// <summary>点搜索框右侧的 ✕：清空并回到搜索之前的视图。</summary>
    private void ClearSearch_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SearchBox.Text.Length == 0) return;

        SearchBox.Text = "";     // 走 TextChanged 里"回原视图"的分支
        FocusKeyboard(true);
    }
    /// <summary>点搜索框里的「?」：把能用什么语法讲清楚（不用记，也不用键盘）。</summary>
    private void SearchHelp_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MessageDialog.Show(this, "搜索能写什么",
            "直接打字：搜名字、编号、路径，也认拼音首字母（打 bd 出「拔刀相向」）。\n\n" +
            "还能写过滤语法，多个条件用空格隔开（都是「并且」）：\n" +
            "  #武侠              带这个标签的\n" +
            "  star:>=4           评分 4 星以上\n" +
            "  dur:<2s            短于 2 秒\n" +
            "  sr:>48k            采样率高于 48kHz\n" +
            "  ch:2               2 声道\n" +
            "  type:wav           只要 wav\n\n" +
            "例：star:>=4 #武侠 dur:<3s  =「4 星以上的武侠短音效」。\n" +
            "写好之后点工具栏的「保存搜索」，就能存成左栏的智能文件夹。");
    }
    // ── 右键菜单（鼠标优先：标签、评分、复制路径、在资源管理器里显示）──

    // ── 集合（#8）──────────────────────────────────────────
    private readonly ObservableCollection<CollectionItem> _collections = new();

    private void RefreshCollections()
    {
        _collections.Clear();
        foreach (var c in _store.Collections)
            _collections.Add(new CollectionItem { Id = c.Id, Name = c.Name, Count = c.Paths.Count });

        CollectionList.ItemsSource = _collections;
        CollectionEmptyHint.Visibility = _store.Collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Collection? FindCollection(string id) => _store.Collections.FirstOrDefault(c => c.Id == id);

    private void CollectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebar) return;
        ClearOtherSidebarLists(CollectionList);
        if (CollectionList.SelectedItem is not CollectionItem item) return;
        if (FindCollection(item.Id) is not { } col) return;

        FocusKeyboard(true);
        LoadCollectionView(col);
    }


    private void CollectionList_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 右键也要把鼠标下面那一行选中，否则菜单根本弹不出来（v0.56 修）
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: CollectionItem hit })
        {
            bool old = _suppressSidebar;
            _suppressSidebar = true;          // 只选中，不跳转视图
            try { CollectionList.SelectedItem = hit; }
            finally { _suppressSidebar = old; }
        }

        if (CollectionList.SelectedItem is not CollectionItem item) return;
        if (FindCollection(item.Id) is not { } col) return;
        e.Handled = true;
        ShowCollectionMenu(col, CollectionList);
    }

    /// <summary>集合行的 ⋯ 按钮：重命名 / 删除。</summary>
    private void CollectionMore_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not CollectionItem item) return;
        if (FindCollection(item.Id) is not { } col) return;
        ShowCollectionMenu(col, sender as FrameworkElement);
    }

    private void ShowCollectionMenu(Collection col, System.Windows.FrameworkElement? target)
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "打开这个集合" };
        open.Click += (_, _) => LoadCollectionView(col);

        var rename = new MenuItem { Header = "重命名集合…" };
        rename.Click += (_, _) =>
        {
            var name = InputDialog.Ask(this, "重命名集合", "集合名称", col.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            col.Name = name.Trim();
            _store.Save();
            RefreshCollections();
            if (_viewKind == ViewKind.Collection) LoadCollectionView(col);
        };

        var del = new MenuItem { Header = "删除集合" };
        del.Click += (_, _) => MessageDialog.Show(this, "删除集合",
            $"「{col.Name}」会从列表里移除（音效文件本身不受影响）。", "删除", () =>
            {
                _store.Collections.Remove(col);
                _store.Save();
                RefreshCollections();
                LoadAllView();
            });

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(del);
        menu.PlacementTarget = target ?? CollectionList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>把当前选中的音效加进某个集合（没有集合就先建一个）。</summary>
    private void AddToCollection(Collection? target)
    {
        if (_selected.Count == 0)
        {
            ShowToast("先选中要加入集合的音效");
            return;
        }

        var col = target;
        if (col is null)
        {
            var name = InputDialog.Ask(this, "新建集合", "集合名称", "我的集合");
            if (string.IsNullOrWhiteSpace(name)) return;
            col = new Collection { Name = name.Trim() };
            _store.Collections.Add(col);
        }

        int added = 0;
        foreach (var a in _selected)
        {
            if (col.Paths.Any(p => string.Equals(p, a.FilePath, StringComparison.OrdinalIgnoreCase))) continue;
            col.Paths.Add(a.FilePath);
            added++;
        }

        _store.Save();
        RefreshCollections();
        ShowToast(added == 0 ? $"已经在「{col.Name}」里了" : $"已加入「{col.Name}」：{added} 个");
    }

    // ── 左栏：标签 / 智能文件夹 ─────────────────────────────
    private bool _suppressPickers;
    private bool _applyingSmart;            // 正在应用智能文件夹，别把它的高亮清掉



    /// <summary>右键智能文件夹：删除。</summary>

    /// <summary>把当前搜索存成智能文件夹。</summary>
    private void SaveSearch_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        var name = InputDialog.Ask(this, "保存为智能文件夹",
            "以后在左栏点一下就能回到这组筛选结果。\n当前条件：" + SearchQuery.Parse(query).Describe(),
            SearchQuery.Parse(query).Describe(), "保存");
        if (name is null) return;

        try { _db?.SaveQuery(name, query); }
        catch (Exception ex) { CrashLog("保存智能文件夹失败：" + ex.Message); }
        RefreshSmartFolders();
        RefreshCollections();
    }
    /// <summary>标签相关的菜单项（右键菜单与卡片上的「＋ 标签」共用）。</summary>
    private List<object> BuildTagItems(AssetItem a)
    {
        var items = new List<object>();

        List<(string Tag, int Count)> all = new();
        try { all = _db?.TagCounts() ?? all; } catch { }

        foreach (var (tag, _) in all)
        {
            var mi = new MenuItem
            {
                Header = "#" + tag,
                IsCheckable = true,
                IsChecked = a.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))
            };
            mi.Click += (_, _) => ToggleTag(a, tag);
            items.Add(mi);
        }
        if (all.Count > 0) items.Add(new Separator());

        var newTag = new MenuItem { Header = "新建标签…" };
        newTag.Click += (_, _) =>
        {
            var name = InputDialog.Ask(this, "新建标签", "标签存在本地索引库里，可以用来搜索、做智能文件夹。", "", "加上");
            if (name is not null) ToggleTag(a, name);
        };
        items.Add(newTag);

        return items;
    }

    /// <summary>点卡片上那行标签：直接弹出标签菜单（不用记右键）。</summary>
    /// <summary>
    /// 侧栏滚轮：里面的 ListBox 各自带一个 ScrollViewer，会把滚轮事件吃掉（哪怕自己没东西可滚），
    /// 所以外层 ScrollViewer 收不到。这里用 Preview 抢在它们前面，自己滚。
    /// </summary>
    private void Sidebar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // 120 = 一格；一格滚 48px，跟系统习惯接近
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.4);
        e.Handled = true;
    }
    // ── 库管理（v0.9）：重命名 / 移除 / 重新扫描 ─────────────
    /// <summary>点库行右侧的「⋯」：重命名 / 重新扫描 / 移除。</summary>




    private static void RevealFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
    /// <summary>在资源管理器里选中这个文件。</summary>
    private static void RevealFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>点卡片上的星星：点第 N 颗 = N 星；再点同一颗 = 清零。纯鼠标操作。</summary>
    private void Star_Click(object sender, RoutedPropertyChangedEventArgs<int> e)
    {
        _canDrag = false;
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;

        int stars = Math.Clamp(e.NewValue, 1, 5);
        int next = a.Rating == stars ? 0 : stars;   // 再点同一颗 = 取消评分
        a.Rating = next;
        _store.For(a.FilePath).Rating = next;
        _store.Save();

        FocusKeyboard(true);
    }

    // ── 文件命名 ────────────────────────────────────────────
    private static readonly Regex IndexRx = new(@"^(?<idx>\d{1,4})[_\-\s]+(?<rest>.+)$", RegexOptions.Compiled);

    private static string ParseIndex(string path)
    {
        var m = IndexRx.Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups["idx"].Value : "";
    }

    private string ParseDisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = IndexRx.Match(name);
        if (m.Success) name = m.Groups["rest"].Value;
        return NameCleaner.Clean(name, _store.CleanupRules);
    }

    private static string ShortLabel(string name) => ShortLabel(name, 12);

    private static string ShortLabel(string name, int max) => name.Length <= max ? name : name[..max] + "…";

    // ── 搜索 ────────────────────────────────────────────────
    private void UpdateSearchAffordances()
    {
        bool has = SearchBox.Text.Length > 0;
        SearchHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchAffordances();
        if (_suppressSearch) return;

        var q = SearchBox.Text.Trim();

        if (q.Length == 0)
        {
            // 清空搜索：回到搜索之前那个视图
            if (_searchMode)
            {
                _searchMode = false;
                ReloadCurrentView(_viewLibRoot);
            }
            return;
        }

        if (!_applyingSmart)
        {
            _suppressPickers = true;
            try { SmartList.SelectedIndex = -1; }
            finally { _suppressPickers = false; }
        }

        if (!_searchMode)
        {
            // 第一次输入：切到"整库搜索"（在库根/文件夹里都能搜到整个库的东西）
            _searchMode = true;
            LoadSearchView();
            SaveSearchPill.Visibility = Visibility.Visible;
        }
        else
        {
            ApplyFilter();
        }

        UpdateSearchStat();
    }

    /// <summary>
    /// 搜索视图：整个库都放进 _all，ApplyFilter 再按关键词筛。
    /// 这样在库根（只显示文件夹卡片、_all 是空的）也能搜到东西。
    /// </summary>


    /// <summary>导航时清掉搜索（否则会停在搜索结果里，让人以为文件夹是空的）。</summary>
    private void ClearSearch()
    {
        if (!_searchMode && string.IsNullOrEmpty(SearchBox.Text)) return;

        _suppressSearch = true;
        try
        {
            SearchBox.Text = "";
        }
        finally { _suppressSearch = false; }
        UpdateSearchAffordances();

        _searchMode = false;
        SaveSearchPill.Visibility = Visibility.Collapsed;

        // 智能文件夹的高亮也跟着退掉（同一时间只允许一处"亮"）
        _suppressPickers = true;
        try { SmartList.SelectedIndex = -1; }
        finally { _suppressPickers = false; }
    }


    private static bool Matches(AssetItem a, string q) =>
        a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || a.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || a.Index.Equals(q, StringComparison.OrdinalIgnoreCase)
        || a.FilePath.Contains(q, StringComparison.OrdinalIgnoreCase);

    // ── 选择 ────────────────────────────────────────────────
    private void AssetCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;
        FocusKeyboard(true);

        // 双击 = 固定播放 / 取消固定。这是唯一允许"鼠标移开还在响"的入口。
        if (e.ClickCount == 2)
        {
            _canDrag = false;
            TogglePin(a);
            e.Handled = true;
            return;
        }

        // #51：Ctrl 加选 / Shift 连选 / 单击单选，统一交给 SelectionViewModel 判定
        var mod = Keyboard.Modifiers;
        if ((mod & ModifierKeys.Control) != 0)
        {
            _selection.Toggle(a);
        }
        else if ((mod & ModifierKeys.Shift) != 0 && _selection.Anchor is not null)
        {
            _selection.SelectRange(_view.ToList(), a);
        }
        else
        {
            _selection.SelectOnly(a);
        }

        ApplySelection();
        _pressPoint = e.GetPosition(this);
        _dragItem = a;
        _canDrag = true;
    }

    /// <summary>把 SelectionViewModel 的结果同步到镜像集合与卡片选中态。</summary>
    private void ApplySelection()
    {
        _selected.Clear();
        foreach (var x in _selection.Items) _selected.Add(x);
        _anchor = _selection.Anchor;
        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (var a in _all) a.IsSelected = _selected.Contains(a);
    }


    private void TogglePin(AssetItem a)
    {
        if (ReferenceEquals(_pinned, a))
        {
            _pinned = null;
            _audio.Stop(60);
            SetPlaying(null);
            UpdatePlayIcon(false);
        }
        else
        {
            _pinned = a;
            _audio.Play(a.FilePath, loop: true);
            SetPlaying(a);
            UpdatePlayIcon(true);
        }
        SyncPinFlags();
    }


    // ── 拖拽幽灵（#29）：拖到 DAW 之前能看到"拖的是什么、几个" ─────
    private Window? _dragGhost;
    private DispatcherTimer? _ghostTimer;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);




    /// <summary>物理像素 → DIP 的缩放（幽灵窗 Left/Top 用的是 DIP）。</summary>
    private double VisualDpi
    {
        get
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
    }

    // ── 悬停试听 ────────────────────────────────────────────



    // ── 播放控制 ────────────────────────────────────────────

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _pinned = null;
        _audio.Stop(60);
        SetPlaying(null);
        UpdatePlayIcon(false);
        SyncPinFlags();
    }


    // ── 播放头刷新：跟着 WPF 的渲染帧走（约 60fps），而不是 50ms 定时器 ──
    private void StartFrames()
    {
        if (_frameHooked) return;
        CompositionTarget.Rendering += OnFrame;
        _frameHooked = true;
    }

    private void StopFrames()
    {
        if (!_frameHooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _frameHooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var a = _nowPlaying;
        if (a is null) { StopFrames(); return; }
        if (!_audio.IsPlaying) return;

        // 1ms 门限：等价于跟着显示器满帧渲染，仅挡掉同一帧的重复回调
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastFrameMs < (_store.PowerSaver ? 15.0 : 1.0)) return;   // 省电档限 60fps
        _lastFrameMs = now;

        double dur = a.DurationMs > 0 ? a.DurationMs : _audio.LengthMs;
        if (dur <= 0) return;

        double pos = _audio.PositionMs;
        double frac = Math.Clamp(pos / dur, 0, 1);

        a.Playhead = frac;
        PlayerScale.ScaleX = frac;          // 变换不触发布局

        string t = Fmt(pos);
        if (t != _lastTime)                 // 文本只在"秒百分位"变化时更新
        {
            _lastTime = t;
            PlayerTime.Text = t + " / " + Fmt(dur);
        }
    }

    /// <summary>鼠标在波形上移动：画一条游标线，指示"点下去会从哪里开始"。</summary>


    /// <summary>鼠标捕获意外丢失（比如切窗口）：把拖动状态收干净，免得播放停不下来。</summary>
    /// <summary>点击波形任意位置：立刻从该位置起播（区间外则重建循环区间）。</summary>
    /// <summary>在波形上按下：从该处开始播（并在按住期间连续拖动定位）。</summary>

    /// <summary>松手：结束拖动定位。</summary>

    /// <summary>把播放位置跳到波形上的某个 x（也负责"没在播就先播"）。</summary>


    private static string Fmt(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    // ── 帧率探针（SV_FPS=1 时每秒把实测帧数写入临时文件）──────────
    private int _fpsCount;
    private DateTime _fpsStart = DateTime.Now;
    private static readonly bool FpsProbe = Environment.GetEnvironmentVariable("SV_FPS") == "1";
    private static readonly string FpsPath = Path.Combine(Path.GetTempPath(), "sv-fps.txt");



    // ── 键盘 ────────────────────────────────────────────────
    // ── 右键「添加到音匣」（#49）──────────────────────────
    /// <summary>启动参数 --add 带来的路径（App 在创建主窗口前塞进来）。</summary>
    public static string? PendingAdd { get; set; }

    /// <summary>把外部文件收进来：确保它所在的文件夹是个库，然后跳到那个文件夹并选中它。</summary>

    private string? _pendingSelect;

    private FolderItem? FindFolderNode(string path)
    {
        FolderItem? Search(FolderItem node)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (var c in node.Children)
            {
                var hit = Search(c);
                if (hit is not null) return hit;
            }
            return null;
        }

        foreach (var n in _side) { var hit = Search(n); if (hit is not null) return hit; }
        return null;
    }
    private void SelectByPath(string file)
    {
        var item = _all.FirstOrDefault(a => string.Equals(a.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        _selected.Clear();
        _selected.Add(item);
        SyncSelection();
        AssetList.ScrollIntoView(item);
        ShowToast("已定位：" + item.DisplayName);
    }
    // ── 元数据（#15）─────────────────────────────────────
    private readonly Dictionary<string, AudioMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);

    private AudioMeta MetaOf(string path)
    {
        if (_metaCache.TryGetValue(path, out var m)) return m;
        m = MetadataReader.Read(path);
        _metaCache[path] = m;
        return m;
    }

    /// <summary>#28：把选中的东西放进剪贴板——文件（DAW 能直接粘）或路径文本。</summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 正在搜索框里打字时，除了 Esc 一律不拦截（空格/F/数字都是正常输入）
        bool typing = Keyboard.FocusedElement is TextBox;
        if (typing && e.Key != Key.Escape) return;

        // #28：Ctrl+C 复制文件本身（拖拽不灵的 DAW 可以直接粘），Ctrl+Shift+C 复制路径
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopySelection(toFiles: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(this, null!);
                e.Handled = true;
                break;

            case Key.F:
                foreach (var a in _selected.ToList())
                {
                    a.IsFavorite = !a.IsFavorite;
                    _store.For(a.FilePath).Favorite = a.IsFavorite;
                }
                if (_selected.Count > 0) { _store.Save(); ApplyFilter(); }
                break;

            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _selected.Clear();
                foreach (var a in _view) _selected.Add(a);
                SyncSelection();
                e.Handled = true;
                break;

            case Key.E when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                _ = ExportAsync();
                break;

            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Text = ""; }
                else { _selected.Clear(); SyncSelection(); }
                break;

            case >= Key.D1 and <= Key.D5:
                RateSelected(e.Key - Key.D0);
                e.Handled = true;
                break;

            case Key.D0:
                RateSelected(0);
                e.Handled = true;
                break;
        }
    }

    private void RateSelected(int stars)
    {
        if (_selected.Count == 0) return;
        foreach (var a in _selected.ToList())
        {
            a.Rating = stars;
            _store.For(a.FilePath).Rating = stars;
        }
        _store.Save();
    }

    // ── 拖入导入 ────────────────────────────────────────────
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        foreach (var p in paths)
        {
            if (Directory.Exists(p)) await AddLibraryAsync(p);
            else if (File.Exists(p) && AudioFormats.IsAudio(p))
            {
                var dir = Path.GetDirectoryName(p);
                if (dir is not null) await AddLibraryAsync(dir);
            }
        }
        e.Handled = true;
    }

    /// <summary>#17：加库前先认一下这个素材包（封面/授权/没下完/版本）。</summary>

    // ── 播放设置：循环模式 / 归一化 / 音量 / 输出设备 ───────────
    private static string LoopLabel(LoopMode m) => m switch
    {
        LoopMode.Smart => "循环：智能",
        LoopMode.Once => "单次播放",
        _ => "循环：整段"
    };

    private void Loop_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (mode, label) in new[]
        {
            (LoopMode.Whole, "整段循环（从头到尾）"),
            (LoopMode.Smart, "智能循环（跳过首尾静音）"),
            (LoopMode.Once, "单次播放（放完即停）")
        })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _audio.Mode == mode };
            item.Click += (_, _) =>
            {
                _audio.Mode = mode;
                LoopText.Text = LoopLabel(mode);
                RestartCurrent();
            };
            menu.Items.Add(item);
        }
        OpenMenu(menu, LoopPill, System.Windows.Controls.Primitives.PlacementMode.Top);
        e.Handled = true;
    }

    private void Norm_Click(object sender, MouseButtonEventArgs e)
    {
        _audio.Normalize = !_audio.Normalize;
        SyncTogglePills();
        _store.Normalize = _audio.Normalize;
        _store.Save();
        e.Handled = true;
    }

    private void Device_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (id, name) in AudioEngine.EnumerateDevices())
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(_audio.DeviceId, id, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) =>
            {
                _audio.SetDevice(id);
                DeviceText.Text = ShortDevice(name);
                _store.DeviceId = id;
                _store.Save();
                RestartCurrent();
            };
            menu.Items.Add(item);
        }
        OpenMenu(menu, DevicePill, System.Windows.Controls.Primitives.PlacementMode.Top);
        e.Handled = true;
    }

    private void RestartCurrent()
    {
        var a = _nowPlaying;
        if (a is null) return;
        if (_audio.Play(a.FilePath, loop: true))
        {
            SetPlaying(a);
            UpdatePlayIcon(true);
        }
    }

    private static void OpenMenu(ContextMenu menu, UIElement target,
                                 System.Windows.Controls.Primitives.PlacementMode mode)
    {
        menu.PlacementTarget = target;
        menu.Placement = mode;
        menu.IsOpen = true;
    }

    // ── 音量滑杆 ────────────────────────────────────────────
    private void Vol_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _volDragging = true;
        VolTrack.CaptureMouse();
        SetVolumeFrom(e.GetPosition(VolTrack).X);
        e.Handled = true;
    }

    private void Vol_MouseMove(object sender, MouseEventArgs e)
    {
        if (_volDragging) SetVolumeFrom(e.GetPosition(VolTrack).X);
    }

    private void Vol_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _volDragging = false;
        VolTrack.ReleaseMouseCapture();
        _store.Volume = _audio.Volume;
        _store.Save();
        e.Handled = true;
    }

    private void SetVolumeFrom(double x)
    {
        double w = VolTrack.ActualWidth;
        if (w <= 0) return;
        _audio.Volume = (float)Math.Clamp(x / w, 0, 1);
        UpdateVolumeUi();
    }

    private void UpdateVolumeUi()
    {
        double w = VolTrack.ActualWidth > 0 ? VolTrack.ActualWidth : 74;
        double v = Math.Clamp(_audio.Volume, 0, 1);
        VolFill.Width = Math.Max(0, w * v);
        VolThumb.Margin = new Thickness(Math.Max(0, w * v - 5), 0, 0, 0);
    }

    // ── 排序 / 深度 ─────────────────────────────────────────
    private static readonly string[] SortNames = { "名称", "时长", "评分", "导入顺序" };

    private void UpdateSortLabel() =>
        SortText.Text = SortNames[_sortMode] + (_sortDesc ? " ↓" : " ↑");

    private void Sort_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        for (int i = 0; i < SortNames.Length; i++)
        {
            int idx = i;
            var item = new MenuItem { Header = SortNames[i], IsCheckable = true, IsChecked = _sortMode == idx };
            item.Click += (_, _) => { _sortMode = idx; UpdateSortLabel(); ApplyFilter(); };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var asc = new MenuItem { Header = "↑ 升序", IsCheckable = true, IsChecked = !_sortDesc };
        asc.Click += (_, _) => { _sortDesc = false; UpdateSortLabel(); ApplyFilter(); };
        menu.Items.Add(asc);

        var desc = new MenuItem { Header = "↓ 降序", IsCheckable = true, IsChecked = _sortDesc };
        desc.Click += (_, _) => { _sortDesc = true; UpdateSortLabel(); ApplyFilter(); };
        menu.Items.Add(desc);

        OpenMenu(menu, SortPill, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        e.Handled = true;
    }

    private void Depth_Click(object sender, MouseButtonEventArgs e)
    {
        _deep = !_deep;
        DepthText.Text = _deep ? "深度：开" : "深度：关";
        SyncTogglePills();
        e.Handled = true;

        if (_currentFolder is not null) OpenFolder(_currentFolder);
        else if (_viewKind == ViewKind.All) LoadAllView();
        else if (_viewKind == ViewKind.Recent) LoadRecentView();
    }

    // ── 批量导出 + CSV ──────────────────────────────────────
    private async void Export_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ExportAsync();
    }



    /// <summary>复制文件 + 写 CSV 清单。返回数量与清单文件名。</summary>
    /// <summary>复制文件 + 写 CSV 清单；带进度回调与取消。</summary>



    // ── 左栏 ────────────────────────────────────────────────
    private void FolderCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderItem f && !f.IsLibrary)
            OpenFolder(f);
        e.Handled = true;
    }


    private void QuickList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQuick) return;                        // 程序性清空选中时不要再触发加载
        ClearOtherSidebarLists(QuickList);
        if (QuickList.SelectedIndex < 0) return;
        FocusKeyboard(true);

        switch (QuickList.SelectedIndex)
        {
            case 0: LoadAllView(); break;
            case 1: LoadFavoritesView(); break;
            case 2: LoadRecentView(); break;
            case 3: LoadTagsView(); break;
            case 4: LoadDuplicatesView(); break;
            case 5: LoadPlayedView(); break;
        }
    }

    // ── 标签页（v0.8）：集中改名 / 删除 ─────────────────────

    private void RefreshTagPage()
    {
        List<(string Tag, int Count)> counts;
        try { counts = _db?.TagCounts() ?? new List<(string, int)>(); }
        catch (Exception ex) { CrashLog("读标签失败：" + ex.Message); counts = new List<(string, int)>(); }

        TagPageList.ItemTemplate = (DataTemplate)FindResource("TagRowTpl");
        TagPageList.ItemsSource = counts.Select(c => new TagRow(c.Tag, c.Count)).ToList();

        int files = counts.Sum(c => c.Count);
        ContentStat.Text = $"{counts.Count} 个标签 · {files} 处标记";

        if (counts.Count == 0)
            ShowEmptyView("还没有标签", "点任意卡片底部的「＋ 标签」就能加；一个音效可以有多个标签。", "\uE8B7");
        else
            HideEmptyViews();
    }

    /// <summary>点标签页的一行：筛出这个标签下的音效。</summary>

    // ── 重复检查（v0.9）────────────────────────────────────

    private void DupRow_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not DuplicateRow row) return;

        var first = row.PathsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null) RevealFile(first);
    }
    /// <summary>打开某个标签下的音效（面包屑：库名 › 标签 › #标签）。</summary>
    private void OpenTag(string tag)
    {
        if (_lib?.Root is null) return;

        ClearSearch();
        _viewKind = ViewKind.TagDetail;
        _currentTag = tag;
        _favoritesOnly = false;
        _currentFolder = null;
        _viewLibRoot = _lib.RootPath;

        ContentTitle.Text = "#" + tag;
        ApplyContentLayout(false, true);

        var trail = TrailFor(null);
        trail.Add(("标签", null, LoadTagsView));       // 这一段可以点回标签页
        trail.Add(("#" + tag, null, null));
        ShowCrumbs(trail);

        ClearSidebarSelection();

        HashSet<string> paths;
        try { paths = _db?.PathsWithTag(tag) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { CrashLog("读标签下的音效失败：" + ex.Message); paths = new(StringComparer.OrdinalIgnoreCase); }

        var rows = ReadAllRows().Where(r => paths.Contains(r.Path)).ToList();
        SetAssets(rows);
        ContentStat.Text = $"{rows.Count} 个音效";

        if (rows.Count == 0)
            ShowEmptyView("这个标签下还没有音效", "回「标签」页点「删除」可以清掉这个标签。", "\uE8B7");
        else HideEmptyViews();
    }



    private string? _pendingTagDelete;
    private string? _currentTag;

    // ── 窗口 ────────────────────────────────────────────────
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMax(); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    // ── 无边框窗口最大化：要限制在"工作区"内，否则会被任务栏挡住（v0.9.1 修复）──
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Services.FileAssociation.WM_COPYDATA)
        {
            try
            {
                var cds = System.Runtime.InteropServices.Marshal.PtrToStructure<Services.FileAssociation.COPYDATASTRUCT>(lParam);
                var bytes = new byte[cds.cbData];
                System.Runtime.InteropServices.Marshal.Copy(cds.lpData, bytes, 0, cds.cbData);
                var path = System.Text.Encoding.UTF8.GetString(bytes);
                Dispatcher.BeginInvoke(new Action(() => _ = AddExternalFileAsync(path)));
            }
            catch { }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_GETMINMAXINFO)
        {
            ClampToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>把最大化的位置和尺寸压到监视器工作区（n 屏幕不含任务栏）。</summary>
    private static void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var full = info.rcMonitor;

                mmi.ptMaxPosition.x = work.left - full.left;
                mmi.ptMaxPosition.y = work.top - full.top;
                mmi.ptMaxSize.x = work.right - work.left;
                mmi.ptMaxSize.y = work.bottom - work.top;

                // 也压住"手动拖到比工作区还大"
                mmi.ptMaxTrackSize.x = work.right - work.left;
                mmi.ptMaxTrackSize.y = work.bottom - work.top;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }
    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMax();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ── 指针跟随高光 + 主题过渡（v0.11）───────────────────
    private readonly System.Diagnostics.Stopwatch _glowClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastGlowMs = -100;
    private bool _glowHooked;

    /// <summary>挂上鼠标跟随；只在窗口内移动时更新光斑位置（节流到 ~60fps）。</summary>
    private void HookPointerGlow()
    {
        if (_glowHooked) return;
        _glowHooked = true;

        MouseMove += (_, e) =>
        {
            double now = _glowClock.Elapsed.TotalMilliseconds;
            if (now - _lastGlowMs < 16) return;          // 60fps 足够，别每个像素都算
            _lastGlowMs = now;

            var p = e.GetPosition(Root);
            PointerGlowShift.X = p.X - PointerGlow.Width / 2;
            PointerGlowShift.Y = p.Y - PointerGlow.Height / 2;
        };

        MouseEnter += (_, _) => AnimateGlow(0.75, 220);
        MouseLeave += (_, _) => AnimateGlow(0.0, 260);
    }

    private void AnimateGlow(double to, int ms)
    {
        PointerGlow.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
    }

    /// <summary>切换深浅色时让内容淡入一下，别硬切。</summary>
    private void PlayThemeTransition()
    {
        ContentRoot.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
    }
    // ── Toast：轻提示（v0.10）─────────────────────────────
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private bool _toastHooked;

    /// <summary>底部中间弹一句轻提示，1.8 秒后淡出，不挡鼠标。</summary>
    /// <summary>SV_THEME_TEST=1：自动打开外观对话框，把结果（或异常）写日志。</summary>
    private async Task ThemeDialogTestIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SV_THEME_TEST") != "1") return;

        var log = Path.Combine(Path.GetTempPath(), "sv-theme.txt");
        try
        {
            await Task.Delay(600);
            File.AppendAllText(log, "opening dialog\n");
            var picked = ThemeDialog.Pick(this, AccentTheme.Base, _store.PowerSaver, _store.Glass, _store.Radius, _store.Mica);
            File.AppendAllText(log, "dialog returned: " + (picked is null ? "<null>" : AccentTheme.ToHex(picked.Result)) + "\n");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(log, "EX: " + ex + "\n"); } catch { }
        }
    }
    /// <summary>开关 Win11 云母底板；失败就退回自己画的背景并提示一句。</summary>
    private void ApplyMica(bool on, bool notify = false)
    {
        if (!on)
        {
            WindowBackdrop.Disable(this);
            SetAmbientOpacity(1);
            return;
        }

        if (!WindowBackdrop.TryEnable(this, _isDark))
        {
            WindowBackdrop.Disable(this);
            SetAmbientOpacity(1);
            if (notify) ShowToast("这个系统不支持云母底板，已保持原样");
            return;
        }

        // 环境光保持原样：壁纸常常是暗的，全靠云母会变成一片黑；
        // 云母透出来的一点点色 + 自己的渐变，才是想要的效果
        SetAmbientOpacity(1);
    }

    /// <summary>环境光渐变的透明度（开云母时收掉，免得两个背景打架）。</summary>
    private void SetAmbientOpacity(double value)
    {
        Ambient1.Opacity = value;
        Ambient2.Opacity = value;
        Ambient3.Opacity = value;
    }

    // ── 左栏宽度（拖动分隔条）─────────────────────────────
    private void SidebarSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SidebarColumn.Width = new GridLength(244);      // 双击回到默认宽度
        _store.SidebarWidth = 244;
        _store.Save();
    }

    /// <summary>拖动结束后把宽度记下来（用 SizeChanged 比每次拖动都存盘省事）。</summary>
    private void SidebarPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        double w = Math.Round(e.NewSize.Width);
        if (w < 150 || w > 560) return;
        if (Math.Abs(_store.SidebarWidth - w) < 2) return;

        _store.SidebarWidth = w;
        _store.Save();
    }
    // ── 批量重命名（#12）─────────────────────────────────
    /// <summary>按清单真的改文件名，并同步索引与用户数据。返回成功数与错误。</summary>

    /// <summary>设置面板（#48）：外观入口、置顶、省电、导出模板、数据目录、重建索引。</summary>
    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = SettingsDialog.Open(this, _store, _isDark, dark =>
        {
            _isDark = dark;
            ApplyTheme(dark);       // 对话框里点就立刻换，等于实时预览
        });
        if (dlg is null) return;

        // 只是点了「看一遍新手引导」：不写任何设置，直接重播
        if (dlg.ReplayOnboarding) { StartOnboarding(); return; }

        // 深浅色（对话框里已经实时应用过，这里落盘）
        _store.Theme = dlg.UseDark ? "dark" : "light";
        // 置顶
        _store.Topmost = dlg.Topmost2;
        Topmost = _store.Topmost;
        SyncSettingsButton();

        // 省电
        _store.PowerSaver = dlg.PowerSaver;

        // 导出模板
        _store.ExportTemplate = dlg.Template;
        _store.Save();

        if (dlg.AppearanceRequested)
        {
            AccentBtn_Click(sender, e);
            return;
        }

        if (dlg.RebuildRequested) RebuildIndex();
    }

    /// <summary>置顶时把设置按钮点亮，免得"为什么它老在最上面"。</summary>
    /// <summary>标题栏图钉：一键切换窗口置顶（和设置面板里的勾选是同一个状态）。</summary>
    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _store.Topmost = !Topmost;
        _store.Save();
        Topmost = _store.Topmost;
        SyncPinButton();
        ShowToast(_store.Topmost ? "窗口置顶：开" : "窗口置顶：关");
    }

    private void SyncPinButton()
    {
        PinBtn.Foreground = (Brush)FindResource(Topmost ? "Accent" : "TextSecondary");
        PinBtn.ToolTip = Topmost
            ? "窗口置顶：开（点一下取消）"
            : "窗口置顶：关（点一下让音匣盖在别的软件上面）";
    }
    private void SyncSettingsButton() => SettingsBtn.Foreground =
        (Brush)FindResource(Topmost ? "Accent" : "TextSecondary");

    /// <summary>重建索引：清掉这个库的索引行，再完整扫一遍。</summary>
    private void RebuildIndex()
    {
        var roots = _store.Libraries.Select(TryFullPath).Where(Directory.Exists).ToList();
        if (roots.Count == 0) return;

        MessageDialog.Show(this, "重建索引",
            $"会清掉 {roots.Count} 个库的索引并重新扫描（评分、标签、收藏都在 store.json，不会丢）。\n\n文件多的时候要等一会儿。",
            "开始重建", () =>
            {
                try
                {
                    foreach (var r in roots) _db?.DropLibrary(Indexer.Normalize(r));
                    _perDirCache.Clear();
                }
                catch (Exception ex) { CrashLog("清索引失败：" + ex.Message); }

                ShowToast("正在重建索引…");
                _ = ScanLibrariesAsync(roots);
            });
    }
    /// <summary>把 store.json 里的主色套上（没设置就用主题默认色）。</summary>
    private void ApplyAccentFromStore()
    {
        var color = AccentTheme.Parse(_store.Accent) ?? AccentTheme.Presets[0].Color;
        AccentTheme.Apply(color);

        // 面板强度（SV_GLASS 可临时压过设置，验收用）
        double glass = _store.Glass > 0 ? _store.Glass : SurfaceTheme.Default;
        if (double.TryParse(Environment.GetEnvironmentVariable("SV_GLASS"), out double forced)) glass = forced;
        double radius = _store.Radius > 0 ? _store.Radius : SurfaceTheme.DefaultRadius;
        if (double.TryParse(Environment.GetEnvironmentVariable("SV_RADIUS"), out double forcedR)) radius = forcedR;
        SurfaceTheme.Apply(glass, radius);
    }

    /// <summary>点调色板：打开外观编辑器，实时预览，确定后写进 store。</summary>
    private void AccentBtn_Click(object sender, RoutedEventArgs e)
    {
        var picked = ThemeDialog.Pick(this, AccentTheme.Base, _store.PowerSaver, _store.Glass, _store.Radius, _store.Mica);
        if (picked is null)
        {
            ApplyAccentFromStore();       // 取消：回到已保存的颜色
            return;
        }

        _store.Accent = AccentTheme.ToHex(picked.Result);
        _store.PowerSaver = picked.PowerSaver;
        _store.Save();

        AccentTheme.Apply(picked.Result);
        ShowToast("主色已更新：" + _store.Accent + (picked.PowerSaver ? " · 省电模式开" : ""));
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ApplyTheme(_isDark);
        _store.Theme = _isDark ? "dark" : "light";
        _store.Save();
    }

    private void SyncTogglePills()
    {
        NormPill.Style = (Style)FindResource(_audio.Normalize ? "ClickPillOn" : "ClickPill");
        NormText.Foreground = _audio.Normalize ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary");

        DepthPill.Style = (Style)FindResource(_deep ? "ClickPillOn" : "ClickPill");
        DepthText.Foreground = _deep ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary");
    }

    private static string ShortDevice(string name) => name.Length > 16 ? name[..16] + "…" : name;

    private void ApplyTheme(bool dark)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        var md = Application.Current.Resources.MergedDictionaries;
        if (md.Count > 0) md[0] = dict; else md.Add(dict);
        SurfaceTheme.SetBaseline(dict);      // 先记住这套主题的面板色，玻璃强度按它缩放

        ApplyAccentFromStore();      // 换字典会丢掉覆盖，重套一次

        PlayThemeTransition();               // 180ms 淡入，别硬切
        Dispatcher.BeginInvoke(new Action(() => { SyncTogglePills(); SyncSettingsButton(); }));
    }
}




