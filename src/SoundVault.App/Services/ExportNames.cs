using System.IO;
using SoundVault.App.Models;

namespace SoundVault.App.Services;

/// <summary>导出文件名的模板展开。</summary>
public static class ExportNames
{
    public const string DefaultTemplate = "{n:00}_{name}";

    /// <summary>
    /// 支持：{n} {n:00} {n:000} {name} {index} {dur} {sr} {ch} {lib}
    /// 非法字符会被替换掉（不然 Windows 建不了文件）。
    /// </summary>
    public static string Expand(string template, int n, AssetItem a, string libName)
    {
        var s = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;

        s = s.Replace("{n:000}", n.ToString("000"))
             .Replace("{n:00}", n.ToString("00"))
             .Replace("{n}", n.ToString())
             .Replace("{name}", a.DisplayName)
             .Replace("{index}", a.Index)
             .Replace("{dur}", (a.DurationMs / 1000.0).ToString("0.00"))
             .Replace("{sr}", a.SampleRate > 0 ? (a.SampleRate / 1000.0).ToString("0.#") + "k" : "")
             .Replace("{ch}", a.Channels > 0 ? a.Channels.ToString() : "")
             .Replace("{lib}", libName);

        s = Sanitize(s);
        return s.Length == 0 ? $"{n:00}_{Sanitize(a.DisplayName)}" : s;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }
}
