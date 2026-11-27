namespace SoundVault.App.Services;

/// <summary>
/// 把一个查询词展开成「一组等价写法」：
///   繁→简、同义词、驼峰拆开、分隔符去掉。
/// 组内任意一个命中就算命中（组之间仍然是"并且"）。
/// </summary>
public static class QueryExpander
{
    private const int MaxVariants = 8;

    public static string[] Expand(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return Array.Empty<string>();

        var list = new List<string>(MaxVariants) { word };
        void Add(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            v = v.Trim();
            if (v.Length == 0) return;
            if (list.Count >= MaxVariants) return;
            if (!list.Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase))) list.Add(v);
        }

        // ① 繁体 → 简体
        string simp = TradSimp.ToSimp(word);
        Add(simp);

        // ② 同义词（含中英互搜）
        foreach (var s in Synonyms.Others(word)) Add(s);
        if (!ReferenceEquals(simp, word))
            foreach (var s in Synonyms.Others(simp)) Add(s);

        // ③ 分隔符统一：door_open / door-open / door.open → "door open"
        string spaced = NormalizeSeparators(word);
        Add(spaced);
        foreach (var part in spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Add(part);

        // ④ 驼峰拆开：RainHeavy → "Rain Heavy"（同时保留紧凑写法 rainheavy）
        string camel = SplitCamel(spaced);
        if (!camel.Equals(spaced, StringComparison.Ordinal)) Add(camel);
        Add(Compact(spaced));

        return list.ToArray();
    }

    /// <summary>把 `_` `-` `.` `/` `\` 统一成空格。</summary>
    public static string NormalizeSeparators(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is '_' or '-' or '.' or '/' or '\\' ? ' ' : ch);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>RainHeavy → Rain Heavy（只在大小写交界处插空格）。</summary>
    public static string SplitCamel(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]))) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>去掉所有分隔符和空格：door open → dooropen。</summary>
    public static string Compact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (!char.IsWhiteSpace(ch) && ch is not ('_' or '-' or '.')) sb.Append(ch);
        return sb.ToString();
    }
}
