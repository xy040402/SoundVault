using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>
/// P3：让搜索接近"秒搜"。
/// 三件事：整库卡片缓存一次、拼音预热、逐字增量过滤 + 输入防抖。
/// </summary>
public partial class MainWindow
{
    /// <summary>整库卡片缓存：换库 / 扫描完成 / 标签或收藏变了才失效。</summary>
    private List<AssetItem>? _searchItems;
    private string _searchItemsRoot = "";
    private int _searchItemsVersion = -1;

    /// <summary>数据版本号：任何会改变"搜得到什么"的操作都 +1。</summary>
    private int _dataVersion;
    private void BumpDataVersion() => _dataVersion++;

    /// <summary>上一次的过滤结果，用于"多打一个字只在结果里再筛"。</summary>
    private string _lastFilterText = "";
    private List<AssetItem>? _lastFilterResult;
    private int _lastFilterSort = -1;
    private bool _lastFilterDesc;
    private bool _lastFilterFav;

    private DispatcherTimer? _searchDebounce;

    private List<AssetItem> SearchItems()
    {
        var root = _globalScope ? "*" : (_lib?.RootPath ?? "");
        if (_searchItems is not null && _searchItemsRoot == root && _searchItemsVersion == _dataVersion)
            return _searchItems;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = ReadAllRows();

        Dictionary<string, List<string>> tags = new(StringComparer.OrdinalIgnoreCase);
        if (_db is not null)
        {
            try { tags = _db.TagsByPath(rows.Select(r => r.Path)); }
            catch (Exception ex) { CrashLog("读取标签失败：" + ex.Message); }
        }

        var items = new List<AssetItem>(rows.Count);
        foreach (var r in rows)
        {
            var item = CreateAsset(r);
            if (tags.TryGetValue(r.Path, out var list)) item.SetTags(list);
            items.Add(item);
        }

        // 拼音在后台算好：不然第一次输入 "bd" 会卡一下（2 万条约 40ms，10 万条就是 200ms）
        try
        {
            System.Threading.Tasks.Parallel.ForEach(items, it =>
            {
                _ = it.PinyinInitials;
                _ = it.PinyinFull;
            });
        }
        catch { }

        _searchItems = items;
        _searchItemsRoot = root;
        _searchItemsVersion = _dataVersion;

        Log.Info("搜索", $"整库缓存已建 {items.Count} 条 用时={sw.ElapsedMilliseconds}ms");
        return items;
    }

    /// <summary>
    /// 后台把整库缓存先建好：用户第一次点搜索时就不用等（10 万条约 1 秒）。
    /// 建缓存只读数据库和文件头，不碰界面，所以可以放线程池。
    /// </summary>
    public void PrewarmSearchCache()
    {
        if (_lib is null) return;
        var root = _lib.RootPath;

        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var n = SearchItems().Count;
                Log.Info("搜索", $"预热门 {Path.GetFileName(root)}：{n} 条 用时={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { Log.Warn("搜索", "预热失败", ex); }
        });
    }

    /// <summary>
    /// 波形补好后，只把新峰值贴回"当前显示"的卡片，避免整库重建。
    /// 返回实际更新了多少张卡。
    /// </summary>
    private int RefreshVisiblePeaks(string root)
    {
        if (_db is null || _view.Count == 0) return 0;

        try
        {
            // 只查当前视图里、峰值还空着的那些
            var need = _view.Where(a => a.PeaksBytes is null
                                        && a.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            .Select(a => a.FilePath)
                            .ToList();
            if (need.Count == 0) return 0;

            var peaks = _db.PeaksFor(need);
            int n = 0;
            foreach (var item in _view)
            {
                if (item.PeaksBytes is not null) continue;
                if (!peaks.TryGetValue(item.FilePath, out var bytes)) continue;
                item.PeaksBytes = bytes;
                n++;
            }
            return n;
        }
        catch (Exception ex)
        {
            Log.Warn("波形", "刷新可见卡片失败", ex);
            return 0;
        }
    }
    /// <summary>
    /// 每补完一批波形就调用：把这批文件的峰值立刻贴回当前显示的卡片。
    /// 用户看到的是"卡片逐个亮起来"，而不是等整库补完才一起亮。
    /// </summary>
    /// <summary>path → 卡片，避免每批回调都遍历整库（10 万条时是 2000 万次比较）。</summary>
    private bool _peaksPatchWarned;
    private readonly Dictionary<string, Models.AssetItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildItemIndex()
    {
        _itemsByPath.Clear();
        foreach (var item in _all) _itemsByPath[item.FilePath] = item;
    }

    private void ApplyPeaksToView(IReadOnlyDictionary<string, byte[]?> map)
    {
        if (map.Count == 0) return;
        if (_itemsByPath.Count == 0)
        {
            // 当前视图里没有音频卡片（比如停在"文件夹列表"），没什么可贴的；只记一次，别刷日志
            if (!_peaksPatchWarned)
            {
                _peaksPatchWarned = true;
                Log.Info("波形", "当前视图没有音频卡片，波形补好后不贴卡片");
            }
            return;
        }

        int n = 0;
        foreach (var kv in map)
        {
            if (kv.Value is null) continue;
            if (!_itemsByPath.TryGetValue(kv.Key, out var item)) continue;
            if (item.PeaksBytes is not null) continue;
            item.PeaksBytes = kv.Value;
            n++;
        }
        if (n > 0) Log.Info("波形", $"界面即时更新 {n} 张卡片的波形");
    }
    /// <summary>输入防抖：连打五个字只过滤一次。</summary>
    private void QueueFilter()
    {
        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= DebounceTick;
        _searchDebounce.Tick += DebounceTick;
        _searchDebounce.Start();
    }

    private void DebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        ApplyFilter();
        UpdateSearchStat();
    }
}
