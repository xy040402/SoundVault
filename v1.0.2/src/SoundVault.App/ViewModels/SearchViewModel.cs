using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App.ViewModels;

/// <summary>
/// 搜索/筛选的模型层（#51）：纯逻辑、不碰任何 WPF 元素，可以脱离界面单测。
/// 负责：解析查询语法 → 过滤 → 排序。视图只负责把结果塞进列表。
/// </summary>
public sealed class SearchViewModel
{
    public string Text { get; private set; } = "";
    public SearchQuery Query { get; private set; } = SearchQuery.Parse("");
    public bool FavoritesOnly { get; set; }

    /// <summary>0=名称 1=时长 2=评分 3=路径</summary>
    public int SortMode { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>查询从有变无、或反之，用来决定要不要切回普通视图。</summary>
    public bool HasQuery => !Query.IsEmpty;

    public void SetText(string? text)
    {
        Text = text ?? "";
        Query = SearchQuery.Parse(Text);
    }

    /// <summary>对一个集合做"收藏 → 筛选 → 排序"，返回新列表。</summary>
    public List<AssetItem> Apply(IEnumerable<AssetItem> source)
    {
        IEnumerable<AssetItem> src = source;

        if (FavoritesOnly) src = src.Where(a => a.IsFavorite);
        if (!Query.IsEmpty) src = src.Where(Query.Matches);

        src = SortMode switch
        {
            0 => SortDescending
                ? src.OrderByDescending(a => a.DisplayName, StringComparer.CurrentCulture)
                : src.OrderBy(a => a.DisplayName, StringComparer.CurrentCulture),
            1 => SortDescending
                ? src.OrderByDescending(a => a.DurationMs)
                : src.OrderBy(a => a.DurationMs),
            2 => SortDescending
                ? src.OrderByDescending(a => a.Rating)
                : src.OrderBy(a => a.Rating),
            _ => SortDescending
                ? src.OrderByDescending(a => a.FilePath, StringComparer.OrdinalIgnoreCase)
                : src.OrderBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase)
        };

        return src.ToList();
    }

    /// <summary>状态栏文案：`12 个结果 · "拔刀" · 4 星以上`。</summary>
    public string Describe(int count)
    {
        var tail = Query.Describe();
        var star = FavoritesOnly ? "只看收藏" : "";
        var extra = string.Join(" · ", new[] { star }.Where(s => s.Length > 0));
        return $"{count} 个结果" + (tail.Length > 0 ? $" · “{tail}”" : "") + (extra.Length > 0 ? " · " + extra : "");
    }
}
