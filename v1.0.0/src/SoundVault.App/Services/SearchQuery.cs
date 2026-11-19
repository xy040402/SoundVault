using System.Globalization;
using System.Text;
using SoundVault.App.Models;

namespace SoundVault.App.Services;

/// <summary>
/// 搜索框里的"过滤语法"。
/// 支持随手的自由词，也支持字段过滤，可以混着写：
///   拔刀              → 名称/路径里含"拔刀"
///   #武侠             → 带标签"武侠"
///   star:&gt;=4         → 评分 ≥ 4
///   dur:&lt;2s          → 时长 &lt; 2 秒（也可以写 dur:&lt;2）
///   sr:&gt;48k          → 采样率 &gt; 48kHz（也可以写 sr:96000）
///   ch:2 / type:wav   → 声道数 / 扩展名
/// 多个条件之间是"并且"。空查询表示"不过滤"。
/// </summary>
public sealed class SearchQuery
{
    public string Text { get; private set; } = "";
    public List<string> Words { get; } = new();
    public List<string> Tags { get; } = new();

    public int? StarMin { get; private set; }
    public double? DurMaxSec { get; private set; }
    public double? DurMinSec { get; private set; }
    public int? SampleRateMin { get; private set; }
    public int? SampleRateMax { get; private set; }
    public int? Channels { get; private set; }
    public string? Extension { get; private set; }

    public bool IsEmpty =>
        Text.Length == 0 && Tags.Count == 0 && StarMin is null && DurMaxSec is null && DurMinSec is null
        && SampleRateMin is null && SampleRateMax is null && Channels is null && Extension is null;

    public static SearchQuery Parse(string? raw)
    {
        var q = new SearchQuery();
        if (string.IsNullOrWhiteSpace(raw)) return q;

        var words = new List<string>();

        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('#'))
            {
                var tag = token[1..].Trim();
                if (tag.Length > 0) q.Tags.Add(tag);
                continue;
            }

            int colon = token.IndexOf(':');
            if (colon > 0)
            {
                string key = token[..colon].ToLowerInvariant();
                string val = token[(colon + 1)..].Trim();
                if (val.Length > 0 && q.ApplyField(key, val)) continue;
            }

            words.Add(token);
        }

        q.Text = string.Join(' ', words);
        q.Words.AddRange(words);
        return q;
    }

    private bool ApplyField(string key, string value)
    {
        switch (key)
        {
            case "star" or "评分":
            {
                // star:>4 是"严格大于 4"，不能和 star:>=4 一个行为
                int? star = ParseInt(value);
                if (star is null) return false;
                StarMin = Strict(value) ? star + 1 : star;
                return true;
            }

            case "dur" or "时长":
            {
                double? sec = ParseDuration(value);
                if (sec is null) return false;
                if (value.StartsWith('<')) DurMaxSec = sec;
                else DurMinSec = Strict(value) ? sec + 0.001 : sec;   // 严格大于：多 1ms
                return true;
            }

            case "sr" or "采样率":
            {
                int? sr = ParseSampleRate(value);
                if (sr is null) return false;
                if (value.StartsWith('<')) SampleRateMax = sr;
                else SampleRateMin = Strict(value) ? sr + 1 : sr;
                return true;
            }

            case "ch" or "声道":
                Channels = ParseInt(value);
                return Channels is not null;

            case "type" or "格式":
                Extension = "." + value.TrimStart('.').ToLowerInvariant();
                return true;

            default:
                return false;     // 不认识的键就当普通词处理
        }
    }

    /// <summary>`>4` 这种严格大于（`>=4` 不算）。</summary>
    private static bool Strict(string value) =>
        value.StartsWith(">") && !value.StartsWith(">=") && !value.StartsWith("=>");
    private static bool MatchesWord(AssetItem a, string w)
    {
        if (a.DisplayName.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.FileName.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.FilePath.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Index.Equals(w, StringComparison.OrdinalIgnoreCase)) return true;

        // 拉丁字母/数字：再加一条拼音匹配（bd → 拔刀相向，badao → 拔刀）
        if (PinyinSearch.IsLatinQuery(w))
        {
            var q = w.ToLowerInvariant();
            if (a.PinyinInitials.Contains(q)) return true;
            if (a.PinyinFull.Contains(q)) return true;
        }
        return false;
    }

    public bool Matches(AssetItem a)
    {
        // 每个自由词都要命中（名字/文件名/路径/编号，或者拼音）
        foreach (var w in Words)
            if (!MatchesWord(a, w)) return false;

        foreach (var tag in Tags)
            if (!a.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return false;

        if (StarMin is int min && a.Rating < min) return false;
        if (DurMaxSec is double dmax && a.DurationMs >= dmax * 1000) return false;
        if (DurMinSec is double dmin && a.DurationMs < dmin * 1000) return false;
        if (SampleRateMin is int smin && a.SampleRate < smin) return false;
        if (SampleRateMax is int smax && a.SampleRate >= smax) return false;
        if (Channels is int ch && a.Channels != ch) return false;
        if (Extension is not null && !a.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>给 UI 看的简短说明（智能文件夹用）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Text.Length > 0) parts.Add(Text);
        foreach (var t in Tags) parts.Add("#" + t);
        if (StarMin is int s) parts.Add($"{s} 星以上");
        if (DurMaxSec is double dm) parts.Add($"短于 {dm:0.#}s");
        if (DurMinSec is double dn) parts.Add($"长于 {dn:0.#}s");
        if (SampleRateMin is int sr) parts.Add($"{sr / 1000.0:0.#}k 以上");
        if (Channels is int c) parts.Add($"{c} 声道");
        if (Extension is not null) parts.Add(Extension.TrimStart('.').ToUpperInvariant());
        return parts.Count == 0 ? "全部" : string.Join(" · ", parts);
    }

    // ── 解析辅助 ────────────────────────────────────────────
    private static int? ParseInt(string v)
    {
        v = v.TrimStart('=', '>', '<', ' ');
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static double? ParseDuration(string v)
    {
        v = v.TrimStart('=', '>', '<', ' ').TrimEnd('s', 'S');
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0 ? d : null;
    }

    private static int? ParseSampleRate(string v)
    {
        v = v.TrimStart('=', '>', '<', ' ').ToLowerInvariant();
        bool kilo = v.EndsWith('k');
        if (kilo) v = v[..^1];
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return null;
        return (int)Math.Round(kilo ? d * 1000 : d);
    }
}
