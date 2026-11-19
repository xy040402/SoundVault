namespace SoundVault.App.Services;

/// <summary>
/// 拼音检索：给每个音效算出"全拼"和"首字母"，让 bd 能搜到"拔刀相向"。
/// 依赖 TinyPinyin.Net（40 KB，纯 C#，无原生库）。
/// </summary>
public static class PinyinSearch
{
    /// <summary>首字母，例如 拔刀相向 → "bdxx"。</summary>
    public static string Initials(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try { return Normalize(TinyPinyin.PinyinHelper.GetPinyinInitials(text)); }
        catch { return ""; }
    }

    /// <summary>全拼（无分隔），例如 拔刀相向 → "badaoxiangxiang"。</summary>
    public static string Full(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try { return Normalize(TinyPinyin.PinyinHelper.GetPinyin(text, "")); }
        catch { return ""; }
    }

    /// <summary>只有字母/数字的输入才值得去比拼音（中文词直接按名字比更快）。</summary>
    public static bool IsLatinQuery(string q)
    {
        if (q.Length == 0) return false;
        foreach (char c in q)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
        return true;
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
