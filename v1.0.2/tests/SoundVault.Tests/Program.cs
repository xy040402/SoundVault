// 极简断言测试（不依赖 NuGet 测试框架，离线可跑）：
//   dotnet run --project tests\SoundVault.Tests
// 全通过退出码 0，有失败退出码 1。
using SoundVault.App.Models;
using SoundVault.App.Services;

int pass = 0, fail = 0;
var failures = new List<string>();

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  ✓ {name}"); }
    else { fail++; failures.Add(name + (detail is null ? "" : $"  ({detail})")); Console.WriteLine($"  ✗ {name}  {detail}"); }
}

void Eq<T>(string name, T expected, T actual) =>
    Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"期望 {expected}，实际 {actual}");

AssetItem A(string name, int rating = 0, double durMs = 2000, int sr = 96000, int ch = 2, bool fav = false, string index = "") => new()
{
    FilePath = @"C:\x\" + name + ".wav",
    DisplayName = name,
    Index = index,
    Rating = rating,
    DurationMs = durMs,
    SampleRate = sr,
    Channels = ch,
    IsFavorite = fav
};

// ── 搜索语法 ───────────────────────────────────────────────
Console.WriteLine("SearchQuery");
{
    var a = A("拔刀相向", rating: 5, durMs: 2080);
    var b = A("冲进房门", rating: 2, durMs: 1770, sr: 48000);
    a.SetTags(new[] { "古风", "兵器" });

    Check("#标签 命中", SearchQuery.Parse("#古风").Matches(a));
    Check("#标签 不命中", !SearchQuery.Parse("#古风").Matches(b));

    Check("star:>=4 命中五星", SearchQuery.Parse("star:>=4").Matches(a));
    Check("star:>=4 排除两星", !SearchQuery.Parse("star:>=4").Matches(b));

    Check("dur:<2s 命中 1.77s", SearchQuery.Parse("dur:<2s").Matches(b));
    Check("dur:<2s 排除 2.08s", !SearchQuery.Parse("dur:<2s").Matches(a));

    Check("sr:>48k 排除 48k", !SearchQuery.Parse("sr:>48k").Matches(b));
    Check("sr:>48k 命中 96k", SearchQuery.Parse("sr:>48k").Matches(a));

    Check("普通词 命中", SearchQuery.Parse("拔刀").Matches(a));
    Check("普通词 不命中", !SearchQuery.Parse("拔刀").Matches(b));

    Check("拼音首字母 bdx 命中", SearchQuery.Parse("bdx").Matches(a));
    Check("拼音全拼 badao 命中", SearchQuery.Parse("badao").Matches(a));

    // 组合：标签 + 评分
    Check("组合查询 #古风 star:>=4", SearchQuery.Parse("#古风 star:>=4").Matches(a));
    Check("组合查询 反例 #古风 star:>=5", !SearchQuery.Parse("#古风 star:>=5").Matches(b));
}

// ── 文件名清洗 ─────────────────────────────────────────────
Console.WriteLine("NameCleaner");
{
    Eq("默认剥版权尾巴", "拔刀相向", NameCleaner.Clean("拔刀相向-叮咚社版权素材", null));
    Eq("默认不误伤普通短横线", "刀-剑-格挡", NameCleaner.Clean("刀-剑-格挡", null) is "刀-剑-格挡" ? "刀-剑-格挡" : NameCleaner.Clean("刀-剑-格挡", null));
    Eq("自定义规则命中", "冲进房门", NameCleaner.Clean("冲进房门-ABC素材库", new[] { "ABC" }));
    Eq("自定义规则不命中就保留", "冲进房门-ABC素材库", NameCleaner.Clean("冲进房门-ABC素材库", new[] { "ZZZ" }));
    Eq("空规则回退默认", "拔刀相向", NameCleaner.Clean("拔刀相向-版权", new string[0]));
}

// ── 导出命名模板 ───────────────────────────────────────────
Console.WriteLine("ExportNames");
{
    var a = A("拔刀相向", durMs: 2080, index: "001");

    Eq("默认模板", "01_拔刀相向", ExportNames.Expand("{n:00}_{name}", 1, a, "古风"));
    Eq("三位序号", "001_拔刀相向", ExportNames.Expand("{n:000}_{name}", 1, a, "古风"));
    Eq("index 占位", "001", ExportNames.Expand("{index}", 1, a, "古风"));
    Eq("dur 占位", "2.08", ExportNames.Expand("{dur}", 1, a, "古风"));
    Eq("sr 占位", "96k", ExportNames.Expand("{sr}", 1, a, "古风"));
    Eq("lib 占位", "古风", ExportNames.Expand("{lib}", 1, a, "古风"));
    Eq("非法字符被替换", "a_b", ExportNames.Expand("a/b", 1, a, "古风"));
}

// ── 响度（LUFS）─────────────────────────────────────────────
Console.WriteLine("Loudness");
{
    const int rate = 48000;
    float[] Sine(double dbfs)
    {
        double amp = Math.Pow(10, dbfs / 20);
        var data = new float[rate * 2];
        for (int i = 0; i < data.Length; i++) data[i] = (float)(Math.Sin(2 * Math.PI * 1000 * i / rate) * amp);
        return data;
    }

    double loud = Loudness.IntegratedLufs(Sine(-6), rate, 1);
    double quiet = Loudness.IntegratedLufs(Sine(-20), rate, 1);

    Check("1kHz -6dBFS ≈ -9 LUFS", Math.Abs(loud + 9) < 0.5, $"实测 {loud:0.00}");
    Check("1kHz -20dBFS ≈ -23 LUFS", Math.Abs(quiet + 23) < 0.5, $"实测 {quiet:0.00}");
    Check("两者差 ≈ 14 LU", Math.Abs((loud - quiet) - 14) < 0.3, $"实测差 {loud - quiet:0.00}");

    // 归一化增益：目标 -18 LUFS
    var gainQuiet = Loudness.GainFor(Sine(-20), rate, 1);
    var gainLoud = Loudness.GainFor(Sine(-6), rate, 1);
    Check("小声被提上来", gainQuiet > 1.5, $"gain={gainQuiet:0.00}");
    Check("大声被压下去", gainLoud < 0.5, $"gain={gainLoud:0.00}");

    // 静音不该被放大
    var silence = new float[rate];
    Eq("静音不放大", 1f, Loudness.GainFor(silence, rate, 1));
}

// ── #51 ViewModel：搜索 / 选中 ─────────────────────────────
Console.WriteLine("SearchViewModel / SelectionViewModel");
{
    var a = A("拔刀相向", rating: 5, index: "001");
    var b = A("冲进房门", rating: 2, durMs: 1770, index: "012");
    var c = A("床上翻身", rating: 4, durMs: 2200, index: "020");
    c.IsFavorite = true;
    var all = new List<AssetItem> { b, c, a };

    var vm = new SoundVault.App.ViewModels.SearchViewModel();
    vm.SetText("");
    Eq("空查询返回全部", 3, vm.Apply(all).Count);

    vm.SetText("拔刀");
    var hit = vm.Apply(all);
    Eq("按名字过滤 1 条", 1, hit.Count);
    Eq("命中的就是拔刀相向", "拔刀相向", hit[0].DisplayName);

    vm.SetText("");
    vm.FavoritesOnly = true;
    Eq("只看收藏", "床上翻身", vm.Apply(all)[0].DisplayName);
    vm.FavoritesOnly = false;

    vm.SortMode = 2;                 // 按评分
    vm.SortDescending = true;
    Eq("按评分倒序第一条是 5 星", "拔刀相向", vm.Apply(all)[0].DisplayName);
    vm.SortDescending = false;
    Eq("按评分正序第一条是 2 星", "冲进房门", vm.Apply(all)[0].DisplayName);

    vm.SetText("star:>=4");
    Eq("过滤语法在 VM 里也生效", 2, vm.Apply(all).Count);

    // 选中集
    var sel = new SoundVault.App.ViewModels.SelectionViewModel();
    sel.SelectOnly(a);
    Eq("单选数量", 1, sel.Count);
    Check("单选包含 a", sel.Contains(a));

    sel.Toggle(b);
    Eq("Ctrl 加选后 2 个", 2, sel.Count);
    sel.Toggle(b);
    Eq("再 Ctrl 变 1 个", 1, sel.Count);

    sel.SelectOnly(a);
    var order = new List<AssetItem> { a, b, c };
    sel.SelectRange(order, c);
    Eq("Shift 连选 3 个", 3, sel.Count);
    Check("锚点没变", ReferenceEquals(sel.Anchor, a));

    sel.Prune(new[] { a, c });
    Eq("Prune 去掉看不见的", 2, sel.Count);
    Check("Prune 后不含 b", !sel.Contains(b));

    sel.Clear();
    Check("清空后为空", sel.IsEmpty);
}
// ── 拼音 ──────────────────────────────────────────────────
Console.WriteLine("PinyinSearch");
{
    Eq("首字母", "bdxx", PinyinSearch.Initials("拔刀相向"));
    Check("拉丁查询识别", PinyinSearch.IsLatinQuery("bdx"));
    Check("中文不算拉丁", !PinyinSearch.IsLatinQuery("拔刀"));
}

Console.WriteLine();
Console.WriteLine($"通过 {pass}，失败 {fail}");
if (fail > 0)
{
    Console.WriteLine();
    Console.WriteLine("失败项：");
    foreach (var f in failures) Console.WriteLine("  - " + f);
    return 1;
}
return 0;
