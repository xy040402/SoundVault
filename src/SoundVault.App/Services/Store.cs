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
    /// <summary>省电档：界面刷新限制 60fps（播放头不再跟满帧）。</summary>
    public bool PowerSaver { get; set; }
    /// <summary>面板玻璃强度 0–1（0.5 = 设计原值）。</summary>
    public double Glass { get; set; } = 0.5;
    /// <summary>手动集合。</summary>
    public List<Collection> Collections { get; set; } = new();
    /// <summary>每个文件最后一次播放的时间（UTC ticks），用于"最近播放"与"已用"角标。</summary>
    public Dictionary<string, long> PlayedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>每个文件最后一次被拖出去用掉的时间（UTC ticks）。</summary>
    public Dictionary<string, long> UsedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>今天有没有用过/播过（同一本地日期）。</summary>
    public bool UsedToday(string path) => IsToday(UsedAt, path) || IsToday(PlayedAt, path);

    public static bool IsToday(Dictionary<string, long> map, string path) =>
        map.TryGetValue(path, out var ticks) && new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().Date == DateTime.Today;

    /// <summary>记一笔播放/使用时间，并把表裁到 2000 条以内。</summary>
    public void Touch(string path, bool used)
    {
        var map = used ? UsedAt : PlayedAt;
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
