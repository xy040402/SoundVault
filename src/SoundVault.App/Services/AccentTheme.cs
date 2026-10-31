using System.Windows;
using System.Windows.Media;

namespace SoundVault.App.Services;

/// <summary>
/// 主色（Accent）。主题字典里那一组 Accent 颜色都是从这个基色算出来的：
/// Accent / AccentSoft(14%) / AccentBorder(50%) / AccentSoftHover(23%) / OnAccent(黑或白) / WavePlayed。
/// 改主色 = 往 Application.Resources 里替换这几个键，DynamicResource 会立刻刷新。
/// </summary>
public static class AccentTheme
{
    public static Color Base { get; private set; } = Color.FromRgb(0x38, 0xBD, 0xF8);

    /// <summary>六套预设（点是按钮，不用记色号）。</summary>
    public static readonly (string Name, Color Color)[] Presets =
    {
        ("天青", Color.FromRgb(0x38, 0xBD, 0xF8)),
        ("紫罗兰", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ("翡翠", Color.FromRgb(0x34, 0xD3, 0x99)),
        ("琥珀", Color.FromRgb(0xF5, 0x9E, 0x0B)),
        ("玫瑰", Color.FromRgb(0xF4, 0x72, 0xB6)),
        ("朱砂", Color.FromRgb(0xEF, 0x44, 0x44)),
    };

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return null;

        try
        {
            return Color.FromRgb(
                Convert.ToByte(s[..2], 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return null; }
    }

    /// <summary>基色够亮就用深色字，否则用白字（保证按钮上的字看得清）。</summary>
    public static Color OnColor(Color c)
    {
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return lum > 0.55 ? Color.FromRgb(0x04, 0x12, 0x1C) : Colors.White;
    }

    /// <summary>把主色应用到应用资源（当前主题字典的第 0 层）。</summary>
    public static void Apply(Color c)
    {
        Base = c;

        var res = Application.Current?.Resources;
        if (res is null) return;

        res["Accent"] = Solid(c);
        res["AccentSoft"] = Solid(c, 0x24);
        res["AccentBorder"] = Solid(c, 0x80);
        res["AccentSoftHover"] = Solid(c, 0x3A);
        res["OnAccent"] = Solid(OnColor(c));
        res["WavePlayed"] = Solid(c);

        // 环境光也跟着主色走一点，免得改完主色背景还是蓝的
        res["AmbientA"] = Ambient(c, 0x3A);
    }

    private static SolidColorBrush Solid(Color c, byte? alpha = null)
    {
        var brush = new SolidColorBrush(alpha is null ? c : Color.FromArgb(alpha.Value, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Ambient(Color c, byte alpha)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.15, 0.05),
            Center = new Point(0.15, 0.05),
            RadiusX = 0.75,
            RadiusY = 0.85
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x10, 0x18, 0x20), 1));
        brush.Freeze();
        return brush;
    }

    // ── RGB / HSV 互转 ──────────────────────────────────────
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
