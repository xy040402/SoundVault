using SoundVault.App.Models;

namespace SoundVault.App.ViewModels;

/// <summary>
/// 选中集的模型层（#51）：纯逻辑，不碰 WPF。
/// 支持：单选 / Ctrl 加选 / Shift 连选（以锚点为起点）/ 全选 / 反选。
/// </summary>
public sealed class SelectionViewModel
{
    private readonly List<AssetItem> _items = new();
    private AssetItem? _anchor;

    public IReadOnlyList<AssetItem> Items => _items;
    public AssetItem? Anchor => _anchor;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public bool Contains(AssetItem item) => _items.Contains(item);

    public void Clear()
    {
        _items.Clear();
        _anchor = null;
    }

    /// <summary>只选这一个，锚点也挪到它。</summary>
    public void SelectOnly(AssetItem item)
    {
        _items.Clear();
        _items.Add(item);
        _anchor = item;
    }

    /// <summary>直接加入（不改动其它项）。</summary>
    public void Add(AssetItem item)
    {
        if (!_items.Contains(item)) _items.Add(item);
    }

    /// <summary>Ctrl 点击：有就移除，没有就加入（锚点跟着点的那一项走）。</summary>
    public void Toggle(AssetItem item)
    {
        if (_items.Contains(item)) _items.Remove(item);
        else _items.Add(item);
        _anchor = item;
    }

    /// <summary>Shift 点击：从锚点到目标，在 order 里做区间选择（会替换原选中）。</summary>
    public void SelectRange(IReadOnlyList<AssetItem> order, AssetItem target)
    {
        if (_anchor is null || order.Count == 0) { SelectOnly(target); return; }

        int a = IndexOf(order, _anchor);
        int b = IndexOf(order, target);
        if (a < 0 || b < 0) { SelectOnly(target); return; }

        if (a > b) (a, b) = (b, a);

        _items.Clear();
        for (int i = a; i <= b; i++) _items.Add(order[i]);
        // 锚点保持不动，这是资源管理器的手感
    }

    public void SelectAll(IEnumerable<AssetItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _anchor = _items.Count > 0 ? _items[0] : null;
    }

    public void Remove(AssetItem item)
    {
        _items.Remove(item);
        if (ReferenceEquals(_anchor, item)) _anchor = _items.Count > 0 ? _items[0] : null;
    }

    /// <summary>列表变化后把已经不存在的项剔掉（筛选/重扫后要用）。</summary>
    public void Prune(IEnumerable<AssetItem> stillVisible)
    {
        var keep = new HashSet<AssetItem>(stillVisible);
        _items.RemoveAll(x => !keep.Contains(x));
        if (_anchor is not null && !keep.Contains(_anchor)) _anchor = null;
    }

    private static int IndexOf(IReadOnlyList<AssetItem> order, AssetItem item)
    {
        for (int i = 0; i < order.Count; i++)
            if (ReferenceEquals(order[i], item)) return i;
        return -1;
    }
}
