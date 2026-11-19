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
        var root = _lib?.RootPath ?? "";
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
