using System.IO;

namespace SoundVault.App.Services;

/// <summary>一个素材包（文件夹）认出来的信息。</summary>
public sealed class PackInfo
{
    public string Root { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string CoverPath { get; init; } = "";
    public List<string> LicenseFiles { get; init; } = new();
    public int IncompleteCount { get; init; }
    public int AudioCount { get; init; }

    /// <summary>侧栏上显示的库名：`古风音效 v4.0`。</summary>
    public string DisplayName => string.IsNullOrEmpty(Version) ? Name : $"{Name} v{Version}";

    public string Summary
    {
        get
        {
            var lines = new List<string> { $"名称：{Name}" };
            if (!string.IsNullOrEmpty(Version)) lines.Add($"版本：v{Version}");
            lines.Add($"音频：{AudioCount} 个文件");
            lines.Add(CoverPath.Length > 0 ? $"封面：{Path.GetFileName(CoverPath)}" : "封面：没找到（logo/cover/文件夹图片）");
            lines.Add(LicenseFiles.Count > 0
                ? "授权文件：" + string.Join("、", LicenseFiles.Take(3).Select(Path.GetFileName))
                : "授权文件：没找到（LICENSE / 授权 / 使用说明）");

            if (IncompleteCount > 0)
                lines.Add($"⚠ 有 {IncompleteCount} 个没下完的文件（.downloading / .part / .!ut 等），建议下完再导入");

            return string.Join("\n", lines);
        }
    }
}

/// <summary>素材包识别（#17）：封面、授权文件、没下完的文件、名字里的版本号。</summary>
public static class PackScanner
{
    private static readonly string[] CoverNames =
    {
        "logo.jpg", "logo.png", "logo.jpeg", "cover.jpg", "cover.png", "cover.jpeg",
        "folder.jpg", "folder.png", "artwork.jpg", "artwork.png", "封面.jpg", "封面.png"
    };

    private static readonly string[] LicenseKeywords =
    {
        "license", "licence", "授权", "许可", "使用说明", "readme", "eula", "terms"
    };

    private static readonly string[] PartialExts =
    {
        ".downloading", ".part", ".crdownload", ".!ut", ".tmp", ".partial", ".bc!", ".opdownload"
    };

    public static PackInfo Scan(string root)
    {
        var info = new PackInfo { Root = root, Name = Path.GetFileName(root.TrimEnd('\\', '/')) };

        string version = ExtractVersion(info.Name);
        string name = version.Length > 0 ? StripVersion(info.Name) : info.Name;

        string cover = "";
        var licenses = new List<string>();
        int partial = 0;
        int audio = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var file = Path.GetFileName(f);
                var lower = file.ToLowerInvariant();
                var ext = Path.GetExtension(lower);

                if (cover.Length == 0 && CoverNames.Contains(lower)) cover = f;
                if (AudioFormats.IsAudio(file)) audio++;
                if (PartialExts.Contains(ext) || lower.EndsWith(".downloading")) partial++;

                if (ext is ".txt" or ".pdf" or ".md" or ".rtf" or ".doc" or ".docx" or ".html" or ".htm")
                {
                    var stem = Path.GetFileNameWithoutExtension(lower);
                    if (LicenseKeywords.Any(k => stem.Contains(k) || lower.Contains(k))) licenses.Add(f);
                }
            }

            audio += Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(f => AudioFormats.IsAudio(f))
                - audio > 0
                ? 0 : 0;   // 递归计数在下面单独算，避免重复
        }
        catch { }

        try
        {
            audio = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(f => AudioFormats.IsAudio(Path.GetFileName(f)));
            partial = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(f => PartialExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || f.EndsWith(".downloading", StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        return new PackInfo
        {
            Root = root,
            Name = name,
            Version = version,
            CoverPath = cover,
            LicenseFiles = licenses,
            IncompleteCount = partial,
            AudioCount = audio
        };
    }

    /// <summary>从文件夹名里抠版本号：`古风音效4.0版本` → `4.0`，`SFX_v2.1` → `2.1`。</summary>
    public static string ExtractVersion(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name,
            @"(?:^|[\s_\-vV（）()第])(\d+\.\d+(?:\.\d+)?)(?=\s*版本|\s*版|[\s_\-（）()]|$)|(\d+\.\d+(?:\.\d+)?)\s*(?:版本|版)");
        if (!m.Success) return "";
        return m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static string StripVersion(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[\s_\-]*\d+\.\d+(?:\.\d+)?\s*(版本|版)?", "").Trim();
}
