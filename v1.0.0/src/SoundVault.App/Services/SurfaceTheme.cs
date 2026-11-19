using System.Windows;
using System.Windows.Media;

namespace SoundVault.App.Services;

/// <summary>
/// 面板"玻璃强度"：把主题里那几组半透明叠层的 alpha 统一按比例缩放。
/// 关键点：**基准色来自当前主题**（深色是白色叠层、浅色是深色墨叠层），不能写死成白色，
/// 否则浅色模式下边框变成"白底上的白线"，整片界面看不见边（v0.18.2 修）。
/// </summary>
public static class SurfaceTheme
{
    public const double Default = 0.5;
    public const double DefaultRadius = 1.0;

    private static readonly string[] Keys =
    {
        "PanelBg", "PanelBorder", "CardBg", "CardHover", "CardBorder",
        "HoverBg", "SearchBg", "ToolbarBg", "PillHoverBg"
    };

    private static readonly (string Key, double Value)[] Radii =
    {
        ("R6", 6), ("R7", 7), ("R8", 8), ("R9", 9), ("R10", 10), ("R12", 12)
    };

    private static readonly Dictionary<string, Color> Baseline = new(StringComparer.Ordinal);

    public static double Current { get; private set; } = Default;
    public static double Radius { get; private set; } = DefaultRadius;

    /// <summary>换主题后调用：把主题自带的颜色记成基准色。</summary>
    public static void SetBaseline(ResourceDictionary theme)
    {
        Baseline.Clear();
        foreach (var key in Keys)
        {
            if (theme[key] is SolidColorBrush b) Baseline[key] = b.Color;
        }
    }

    /// <summary>强度 0–1、圆角倍率 0–1.6（1 = 设计原值）。</summary>
    public static void Apply(double strength, double radiusScale)
    {
        Current = Math.Clamp(strength, 0, 1);
        Radius = Math.Clamp(radiusScale, 0, 1.6);

        var res = Application.Current?.Resources;
        if (res is null) return;

        // 0 → ×0.35（很透）  0.5 → ×1.0（主题原值）  1 → ×1.65（很实）
        double factor = 0.35 + 1.3 * Current;

        foreach (var key in Keys)
        {
            if (!Baseline.TryGetValue(key, out var baseColor)) continue;

            byte a = (byte)Math.Clamp(Math.Round(baseColor.A * factor), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();
            res[key] = brush;
        }

        foreach (var (key, value) in Radii)
            res[key] = new CornerRadius(Math.Round(value * Radius, 1));
    }
}