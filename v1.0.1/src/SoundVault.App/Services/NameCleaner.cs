namespace SoundVault.App.Services;

/// <summary>
/// 从文件名里剥掉"版权/素材"这类尾巴，得到干净的显示名。
/// 规则可配（设置面板里一行一条），默认是"版权、素材"——和 v0.6 的硬编码行为一致。
/// </summary>
public static class NameCleaner
{
    public static readonly string[] DefaultRules = { "版权", "素材" };

    /// <summary>把规则串整理成非空列表；没配就用默认。</summary>
    public static List<string> Normalize(IEnumerable<string>? rules)
    {
        var list = rules?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList() ?? new List<string>();

        return list.Count > 0 ? list : DefaultRules.ToList();
    }

    /// <summary>
    /// 只在"最后一个短横线之后的那一段"里找关键词，找到就把这一段整段去掉。
    /// 例：`拔刀相向-叮咚社版权素材` → `拔刀相向`。
    /// </summary>
    public static string Clean(string name, IEnumerable<string>? rules)
    {
        if (string.IsNullOrEmpty(name)) return "";

        var list = Normalize(rules);
        int dash = name.LastIndexOf('-');
        if (dash > 0 && dash < name.Length - 1)
        {
            var tail = name[(dash + 1)..];
            foreach (var rule in list)
            {
                if (tail.Contains(rule, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..dash].Trim();
                }
            }
        }
        return name.Trim();
    }
}
