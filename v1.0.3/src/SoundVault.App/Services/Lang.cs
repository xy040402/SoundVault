using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoundVault.App.Services;

/// <summary>界面语言：跟随系统 / 简体 / 繁体 / English。以中文原文为 key。</summary>
public static class Lang
{
    public const string Auto = "auto", Hans = "zh-Hans", Hant = "zh-Hant", En = "en";

    public static string Current { get; private set; } = Auto;

    public static string Effective
    {
        get
        {
            if (Current != Auto) return Current;
            var c = System.Globalization.CultureInfo.CurrentUICulture;
            if (c.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return c.Name.Contains("TW", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? Hant : Hans;
            return En;
        }
    }

    public static void Set(string lang) => Current = string.IsNullOrWhiteSpace(lang) ? Auto : lang;

    public static string T(string zh)
    {
        switch (Effective)
        {
            case En: return En_.TryGetValue(zh, out var en) ? en : zh;
            case Hant: return ToHant(zh);
            default: return zh;
        }
    }

    // ── 遍历界面树换文字 ────────────────────────────────────
    private static readonly Dictionary<TextBlock, string> Origin = new();

    /// <summary>
    /// 把界面上的中文换成当前语言。
    /// 同时走可视树和逻辑树（模板/ContentPresenter 里的东西在逻辑树里），
    /// 遇到 RowDefinition 这种非 Visual 的对象直接跳过，不能抛。
    /// </summary>
    public static int ApplyTo(DependencyObject root)
    {
        int n = 0;
        try
        {
            var stack = new Stack<DependencyObject>();
            var seen = new HashSet<DependencyObject>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is null || !seen.Add(node)) continue;

                if (node is TextBlock tb && Translate(tb)) n++;

                // 可视子树
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                {
                    int count = 0;
                    try { count = VisualTreeHelper.GetChildrenCount(node); } catch { count = 0; }
                    for (int i = 0; i < count; i++)
                    {
                        try { stack.Push(VisualTreeHelper.GetChild(node, i)); } catch { }
                    }
                }

                // 逻辑子树（模板内容、ContentPresenter 的 Content 等）
                try
                {
                    foreach (var child in LogicalTreeHelper.GetChildren(node))
                        if (child is DependencyObject d) stack.Push(d);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("语言", "遍历界面失败", ex);
        }

        if (n > 0 || Environment.GetEnvironmentVariable("SV_LANG_TRACE") == "1")
            Log.Info("语言", $"applied={n} effective={Effective} current={Current}");

        return n;
    }

    /// <summary>整串先查；查不到就把尾部的 ↑ / ↓ / … / 空格去掉再查，保留后缀。</summary>
    private static string TranslateSmart(string zh)
    {
        var direct = T(zh);
        if (Effective == En && !string.Equals(direct, zh, StringComparison.Ordinal)) return direct;

        // 换行/空格归一后再查
        if (Effective == En && EnNorm.TryGetValue(Norm(zh), out var normHit)) return normHit;
        if (!string.Equals(direct, zh, StringComparison.Ordinal)) return direct;

        foreach (var suffix in new[] { " ↑", " ↓", "…", "...", " " })
        {
            if (!zh.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var core = zh[..^suffix.Length];
            var hit = T(core);
            if (!string.Equals(hit, core, StringComparison.Ordinal)) return hit + suffix;
        }
        return zh;
    }

    /// <summary>把换行/连续空格压成单个空格，运行时拼出来的长文案也能对上。</summary>
    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            if (ch is (char)10 or (char)13 or (char)9 or (char)32)
            {
                if (!space) { sb.Append((char)32); space = true; }
                continue;
            }
            sb.Append(ch);
            space = false;
        }
        return sb.ToString().Trim();
    }

    // 注意：不能用 ToDictionary —— 词条里有归一化后重名的，会抛异常把整个语言模块搞挂
    private static Dictionary<string, string>? _norm;
    private static Dictionary<string, string> EnNorm => _norm ??= BuildNorm();

    private static Dictionary<string, string> BuildNorm()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in En_)
        {
            var k = Norm(kv.Key);
            if (!map.ContainsKey(k)) map[k] = kv.Value;
        }
        return map;
    }

    private static bool Translate(TextBlock tb)
    {
        try
        {
            var src = tb.Text;
            if (string.IsNullOrWhiteSpace(src)) return false;

            if (!Origin.TryGetValue(tb, out var zh))
            {
                zh = src;
                Origin[tb] = zh;
            }

            var to = TranslateSmart(zh);
            if (string.Equals(src, to, StringComparison.Ordinal)) return false;

            tb.Text = to;
            return true;
        }
        catch { return false; }
    }

    // ── 繁简 ────────────────────────────────────────────────
    private static readonly Dictionary<char, char> SimpToTrad = BuildSimpToTrad();

    private static Dictionary<char, char> BuildSimpToTrad()
    {
        var map = new Dictionary<char, char>();
        foreach (var pair in TradSimp.ReversePairs())
            if (!map.ContainsKey(pair.Key)) map[pair.Key] = pair.Value;

        var extra = new (char S, char T)[]
        {
            ('设','設'),('置','置'),('语','語'),('图','圖'),('标','標'),('签','籤'),('声','聲'),
            ('乐','樂'),('库','庫'),('夹','夾'),('导','導'),('删','刪'),('这','這'),('个','個'),
            ('里','裡'),('没','沒'),('还','還'),('点','點'),('击','擊'),('开','開'),('关','關'),
            ('闭','閉'),('显','顯'),('隐','隱'),('复','複'),('制','製'),('贴','貼'),('选','選'),
            ('评','評'),('收','收'),('藏','藏'),('播','播'),('暂','暫'),('循','環'),('环','環'),
            ('深','深'),('度','度'),('长','長'),('称','稱'),('时','時'),('采','採'),('样','樣'),
            ('输','輸'),('题','題'),('种','種'),('删','刪'),('录','錄'),('频','頻'),('数','數'),
            ('据','據'),('统','統'),('计','計'),('检','檢'),('测','測'),('试','試'),('听','聽'),
            ('认','認'),('识','識'),('扫','掃'),('描','描'),('索','索'),('索','索'),('检','檢')
        };
        foreach (var (s, t) in extra) if (!map.ContainsKey(s)) map[s] = t;
        return map;
    }

    public static string ToHant(string zh)
    {
        if (string.IsNullOrEmpty(zh)) return zh;
        var sb = new System.Text.StringBuilder(zh.Length);
        foreach (var ch in zh) sb.Append(SimpToTrad.TryGetValue(ch, out var t) ? t : ch);
        return sb.ToString();
    }

    // ── 英文词条 ────────────────────────────────────────────
    private static readonly Dictionary<string, string> En_ = new()
    {
        ["音匣"] = "SoundVault",
        ["关于"] = "About",
        ["搜索音效、标签、拼音首字母…"] = "Search sounds, tags, pinyin…",
        ["全部音效"] = "All sounds",
        ["收藏"] = "Favorites",
        ["最近导入"] = "Recently added",
        ["标签"] = "Tags",
        ["重复检查"] = "Duplicates",
        ["最近播放"] = "Recently played",
        ["智能文件夹"] = "Smart folders",
        ["集合"] = "Collections",
        ["库"] = "Libraries",
        ["素材文件夹"] = "Library folder",
        ["＋ 拖入文件夹新建库"] = "+ Drop a folder",
        ["只建索引，不复制文件"] = "Index only, files stay put",
        ["全部素材"] = "All assets",
        ["名称"] = "Name",
        ["时长"] = "Duration",
        ["评分"] = "Rating",
        ["导入顺序"] = "Added",
        ["导出"] = "Export",
        ["深度：关"] = "Deep: off",
        ["深度：开"] = "Deep: on",
        ["保存搜索"] = "Save search",
        ["选一个音效"] = "Select a sound",
        ["规格、评分、收藏、标签和片段选区都会显示在这里"] = "Specs, rating, tags and clip range appear here",
        ["片段"] = "Clip",
        ["入点"] = "In",
        ["出点"] = "Out",
        ["试听"] = "Preview",
        ["清空"] = "Clear",
        ["整段"] = "Whole",
        ["播放头"] = "Playhead",
        ["格式"] = "Format",
        ["采样率"] = "Sample rate",
        ["位深"] = "Bit depth",
        ["声道"] = "Channels",
        ["大小"] = "Size",
        ["单声道"] = "Mono",
        ["立体声"] = "Stereo",
        ["收藏到"] = "Add to favorites",
        ["标签会参与搜索"] = "Tags are searchable",
        ["加标签，回车添加"] = "Type a tag, press Enter",
        ["输入标签，回车添加（会同时加入检索）"] = "Type a tag, press Enter",
        ["渲染"] = "Render",
        ["设置"] = "Settings",
        ["常规"] = "General",
        ["外观"] = "Appearance",
        ["浏览"] = "Browsing",
        ["滚动"] = "Scrolling",
        ["导出与命名"] = "Export & naming",
        ["数据与日志"] = "Data & logs",
        ["支持作者"] = "Support",
        ["鼠标悬停自动试听（关掉后只能点「试听」）"] = "Auto-preview on hover",
        ["托盘常驻（关窗后留在右下角）"] = "Keep in system tray",
        ["开机自启（最小化到托盘）"] = "Start with Windows",
        ["界面语言"] = "Language",
        ["跟随系统"] = "System default",
        ["简体中文"] = "Simplified Chinese",
        ["繁體中文"] = "Traditional Chinese",
        ["English"] = "English",
        ["全局快捷键"] = "Global hotkey",
        ["录制"] = "Record",
        ["恢复默认"] = "Reset",
        ["这个文件夹里没有音频"] = "No audio in this folder",
        ["还没有收藏"] = "No favorites yet",
        ["没有找到匹配的音效"] = "No matching sounds",
        ["没有匹配的音效"] = "No matches",
        ["把工具栏的「深度」打开，可以把子文件夹里的音效也列出来。"] = "Turn on Deep to include subfolders.",
        ["点卡片右上角的爱心即可收藏，收藏过的声音都会集中在这里。"] = "Click the heart on a card to favorite it.",
        ["快捷搜索"] = "Quick search",
        ["打开音匣"] = "Open SoundVault",
        ["退出"] = "Quit",
        ["循环：整段"] = "Loop: whole",
        ["归一化"] = "Normalize",
        ["扬声器：默认"] = "Speaker: default",
        ["悬停任意音效即可循环试听"] = "Hover a sound to preview",
        ["正在建立索引…"] = "Indexing…",
        ["音匣免费、开源、无广告，也没有付费版。如果它帮到了你，可以请作者喝杯咖啡——完全自愿。"] =
            "SoundVault is free, open-source and ad-free. If it helped you, you can buy the author a coffee — entirely optional.",
        ["微信赞赏"] = "WeChat tip",
        ["支付宝"] = "Alipay",
        ["爱发电"] = "Afdian",
        ["导出范围"] = "Range",
        ["只导出警告和错误（WARN / ERROR）"] = "Warnings and errors only",
        ["取消"] = "Cancel",
        ["关闭"] = "Close",
        ["保存图片"] = "Save image",
        ["确定"] = "OK",
        ["应用"] = "Apply",
        ["保存"] = "Save",
        ["素材包"] = "Pack",
        ["快捷入口"] = "Quick access",
        ["搜索后点「保存搜索」"] = "Save a search from the box",
        ["选中音效后右键「添加到集合」"] = "Right-click a sound to add to a collection",
        ["音效"] = "Sounds",
        ["把素材包文件夹拖到左边"] = "Drop a sound pack folder here",
        ["音匣会递归扫描其中的音频文件、建立波形与索引。"] = "SoundVault scans it recursively and builds waveforms.",
        ["只建索引：不移动、不复制、不修改你的任何文件。"] = "Index only: nothing is moved, copied or modified.",
        ["选择文件夹…"] = "Choose folder…",
        ["扬声器：默认"] = "Speaker: default",
        ["名称 ↑"] = "Name ↑",
        ["名称 ↓"] = "Name ↓",
        ["深度：关"] = "Deep: off",
        ["深度：开"] = "Deep: on",
        ["还没有音效"] = "No sounds yet",
        ["添加文件夹"] = "Add folder",
        ["打开文件夹"] = "Open folder",
        ["音匣会递归扫描其中的音频文件，建立波形与索引。 只建索引：不移动、不复制、不修改你的任何文件。"] =
            "SoundVault scans it recursively and builds waveforms. Index only: nothing is moved or modified.",
        ["音匣会递归扫描其中的音频文件，建立波形与索引。"] = "SoundVault scans it recursively and builds waveforms.",
        ["只建索引：不移动、不复制、不修改你的任何文件。"] = "Index only: nothing is moved or modified.",
        ["扬声器 · 默认"] = "Speaker · default",
        ["扬声器"] = "Speaker",
    };
}
