using System;
using System.Collections.Generic;

namespace SoundVault.App.Services;

/// <summary>
/// 自然排序（人类顺序）：把连续数字当成数字比，而不是逐字符比。
/// 例：1_ / 2_ / 9_ / 10_ / 11_ / 100_  「10」排在「9」后面，而不是排在「1」后面。
/// </summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];

            if (IsDigit(ca) && IsDigit(cb))
            {
                // 取两边的完整数字段
                int si = i, sj = j;
                while (i < a.Length && IsDigit(a[i])) i++;
                while (j < b.Length && IsDigit(b[j])) j++;

                var na = a.AsSpan(si, i - si);
                var nb = b.AsSpan(sj, j - sj);

                // 去掉前导 0 再比长度（长度不同说明数量级不同）
                var ta = TrimZeros(na);
                var tb = TrimZeros(nb);
                if (ta.Length != tb.Length) return ta.Length < tb.Length ? -1 : 1;

                int cmpNum = ta.SequenceCompareTo(tb);
                if (cmpNum != 0) return cmpNum < 0 ? -1 : 1;

                // 数值相等时：位数少的排前面（007 < 7 这种按原样稳定一下）
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;
                continue;
            }

            char la = char.ToLowerInvariant(ca), lb = char.ToLowerInvariant(cb);
            if (la != lb) return la < lb ? -1 : 1;
            i++; j++;
        }

        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return 0;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static ReadOnlySpan<char> TrimZeros(ReadOnlySpan<char> s)
    {
        int k = 0;
        while (k < s.Length - 1 && s[k] == '0') k++;
        return s[k..];
    }

    /// <summary>给 LINQ 用的比较器。</summary>
    public static readonly IComparer<string?> Values = Instance;


}
