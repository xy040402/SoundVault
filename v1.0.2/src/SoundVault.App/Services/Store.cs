using System.IO;
using System.Text.Json;

namespace SoundVault.App.Services;

public sealed class UserAsset
{
    public int Rating { get; set; }
    public bool Favorite { get; set; }
}

/// <summary>用户数据：导入过的素材包、评分/收藏、主题。存 %LocalAppData%\SoundVault\store.json</summary>
/// <summary>集合：手动把几个音效攒成一组（存绝对路径，跨文件夹/跨库都行）。</summary>
public sealed class Collection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新集合";
    public List<string> Paths { get; set; } = new();
}

public sealed class Store
{
    public List<string> Libraries { get; set; } = new();
    public Dictionary<string, UserAsset> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>库的显示名（路径 → 自定义名字）；没设就用文件夹名。</summary>
    public Dictionary<string, string> LibraryNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Theme { get; set; } = "dark";
    public double Volume { get; set; } = 0.85;
    public bool Normalize { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>主色（#RRGGBB）；空 = 用主题默认色。</summary>
    public string? Accent { get; set; }

    /// <summary>主色历史（最近用过的，最新在前）与手动收藏的颜色，外观页里点一下就能换回来。</summary>
    public List<string> AccentHistory { get; set; } = new();
    public List<string> AccentFavorites { get; set; } = new();
    /// <summary>面板玻璃强度 0–1（0.5 = 设计原值）。</summary>
    public double Glass { get; set; } = 0.5;
    /// <summary>手动集合。</summary>
    public List<Collection> Collections { get; set; } = new();
    /// <summary>每个文件最后一次播放的时间（UTC ticks），用于"最近播放"。</summary>
    public Dictionary<string, long> PlayedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>每个文件最后一次被拖出去用掉的时间（UTC ticks）。</summary>
    /// <summary>显示名清洗规则（一行一条，匹配就剥掉最后一个短横线之后的那段）。</summary>
    public List<string> CleanupRules { get; set; } = new() { "版权", "素材" };
    /// <summary>是否已经看过新手引导。</summary>
    public bool Onboarded { get; set; }
    /// <summary>左栏宽度（像素）。</summary>


    public double SidebarWidth { get; set; } = 244;
    /// <summary>窗口置顶。</summary>

    public bool Topmost { get; set; }
    /// <summary>导出文件命名模板（占位符见 ExportNames）。</summary>

    public string ExportTemplate { get; set; } = "{n:00}_{name}";
    /// <summary>圆角倍率（1 = 设计原值，0 = 直角）。</summary>
    public double Radius { get; set; } = 1.0;
    /// <summary>Windows 11 云母（Mica）底板；系统不支持会自动退回。</summary>
    public bool Mica { get; set; }

    /// <summary>滚轮是否走平滑动画（关掉就是一步到位）。</summary>
    public bool ScrollAnim { get; set; } = true;

    /// <summary>滚动动画时长（毫秒）。100 = 快，140 = 标准，220 = 缓。</summary>
    public int ScrollAnimMs { get; set; } = 140;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Dir => AppPaths.DataDir;   // 便携模式下会指到 exe 旁边的 data\

    private static string FilePath => AppPaths.StorePath;

    /// <summary>文件改名后把评分/收藏的键也跟着挪（不然用户数据就丢了）。</summary>
    public void RenameAsset(string oldPath, string newPath)
    {
        if (Assets.TryGetValue(oldPath, out var v))
        {
            Assets.Remove(oldPath);
            Assets[newPath] = v;
        }
        if (LibraryNames.Count > 0)
        {
            var keys = LibraryNames.Keys.Where(k => k.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in keys)
            {
                var v2 = LibraryNames[k];
                LibraryNames.Remove(k);
                LibraryNames[newPath + k[oldPath.Length..]] = v2;
            }
        }
    }

    /// <summary>记一笔播放/使用时间，并把表裁到 2000 条以内。</summary>
    public void Touch(string path)
    {
        var map = PlayedAt;
        map[path] = DateTime.UtcNow.Ticks;
        if (map.Count > 2000)
        {
            var keep = map.OrderByDescending(kv => kv.Value).Take(1500).ToList();
            map.Clear();
            foreach (var kv in keep) map[kv.Key] = kv.Value;
        }
    }

    public static Store Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Store>(json, Json);
                if (s is not null)
                {
                    s.Assets = new Dictionary<string, UserAsset>(s.Assets, StringComparer.OrdinalIgnoreCase);
                    s.LibraryNames = new Dictionary<string, string>(s.LibraryNames, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { /* 配置坏了就重置，不能拦住启动 */ }
        return new Store();
    }

    /// <summary>
    /// 删库时把这个库名下的用户数据一起清掉：评分/收藏、播放记录、以及集合里的引用。
    /// 不清的话，重新导入同一个文件夹会"记得"上次的评分 —— 用户看到的是脏数据。
    /// </summary>
    public (int Assets, int Played, int CollectionItems) PurgeLibrary(string root)
    {
        string prefix;
        try { prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\"; }
        catch { prefix = root.TrimEnd('\\') + "\\"; }

        bool Under(string p) => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        int assets = 0, played = 0, colItems = 0;

        foreach (var k in Assets.Keys.Where(Under).ToList()) { Assets.Remove(k); assets++; }
        foreach (var k in PlayedAt.Keys.Where(Under).ToList()) { PlayedAt.Remove(k); played++; }

        foreach (var c in Collections) colItems += c.Paths.RemoveAll(Under);

        Save();
        return (assets, played, colItems);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    /// <summary>库的显示名：有自定义就用自定义，否则用文件夹名。</summary>
    public string NameOf(string root) =>
        LibraryNames.TryGetValue(root, out var alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias
            : Path.GetFileName(root);

    public UserAsset For(string path)
    {
        if (!Assets.TryGetValue(path, out var ua))
        {
            ua = new UserAsset();
            Assets[path] = ua;
        }
        return ua;
    }
}
