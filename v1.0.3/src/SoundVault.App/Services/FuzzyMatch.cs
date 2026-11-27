using System;
using System.Collections.Generic;
using System.Linq;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App.Services;

/// <summary>
/// 字面搜不到时的「可能相关」：不依赖模型，纯本地。
/// 打分 = 名称字符二元组相似度（Dice） + 拼音包含 + 同义词命中。
/// </summary>
public static class FuzzyMatch
{
    /// <summary>把 query 和条目做相似度打分，0~1。</summary>
    public static double Score(string query, AssetItem a)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        string q = query.Trim().ToLowerInvariant();
        string name = a.DisplayName.ToLowerInvariant();
        if (name.Length == 0) return 0;

        double dice = DiceBigram(q, name);

        // 拼音首字母包含（bd → 拔刀）
        double py = 0;
        if (PinyinSearch.IsLatinQuery(q))
        {
            if (a.PinyinInitials.Contains(q, StringComparison.OrdinalIgnoreCase)) py = 0.55;
            else if (a.PinyinFull.Contains(q, StringComparison.OrdinalIgnoreCase)) py = 0.45;
        }

        // 同义词：查询词的任一写法出现在名字里
        double syn = 0;
        foreach (var v in QueryExpander.Expand(q))
        {
            if (v.Length < 2) continue;
            if (name.Contains(v.ToLowerInvariant(), StringComparison.Ordinal)) { syn = 0.5; break; }
        }

        return Math.Clamp(Math.Max(dice, Math.Max(py, syn)), 0, 1);
    }

    /// <summary>字符二元组的 Dice 系数。</summary>
    private static double DiceBigram(string a, string b)
    {
        var ba = Bigrams(a);
        var bb = Bigrams(b);
        if (ba.Count == 0 || bb.Count == 0) return 0;

        int hit = 0;
        var copy = new List<string>(bb);
        foreach (var g in ba)
        {
            int i = copy.IndexOf(g);
            if (i < 0) continue;
            hit++;
            copy.RemoveAt(i);
        }
        return 2.0 * hit / (ba.Count + bb.Count);
    }

    private static List<string> Bigrams(string s)
    {
        var list = new List<string>(Math.Max(0, s.Length - 1));
        for (int i = 0; i + 1 < s.Length; i++)
        {
            char c1 = s[i], c2 = s[i + 1];
            if (char.IsWhiteSpace(c1) || char.IsWhiteSpace(c2)) continue;
            list.Add(new string(new[] { c1, c2 }));
        }
        return list;
    }

    /// <summary>从候选里挑出最相似的若干条（排除已经命中的）。</summary>
    public static List<AssetItem> Pick(string query, IEnumerable<AssetItem> pool,
                                       HashSet<string> exclude, int limit = 20, double min = 0.34)
    {
        var scored = new List<(AssetItem Item, double Score)>();
        foreach (var a in pool)
        {
            if (exclude.Contains(a.FilePath)) continue;
            double s = Score(query, a);
            if (s < min) continue;
            scored.Add((a, s));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.DisplayName.Length)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }
}
