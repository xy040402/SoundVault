using System.Windows;
using System.Windows.Controls;

namespace SoundVault.App.Controls;

/// <summary>
/// 会把整行撑满的换行面板：列数按 MinItemWidth 算，然后把可用宽度**平分**给这些列。
/// 用固定宽度的 WrapPanel 时，列数取整后剩下的宽度会空在右边（v0.18.6）。
/// 子项要设 HorizontalAlignment=Stretch 才会跟着撑开。
/// </summary>
public class FillWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(FillWrapPanel),
        new FrameworkPropertyMetadata(230.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(FillWrapPanel),
        new FrameworkPropertyMetadata(88.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int Columns => Math.Max(1, (int)Math.Floor(AvailableWidth / Math.Max(1, MinItemWidth)));

    private double AvailableWidth { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? MinItemWidth : availableSize.Width;
        AvailableWidth = Math.Max(1, w);

        int cols = Columns;
        double cellW = Math.Max(1, w / cols);
        double cellH = Math.Max(1, ItemHeight);

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(cellW, cellH));

        int rows = InternalChildren.Count == 0 ? 0 : (int)Math.Ceiling(InternalChildren.Count / (double)cols);
        return new Size(w, rows * cellH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double w = Math.Max(1, finalSize.Width);
        int cols = Math.Max(1, (int)Math.Floor(w / Math.Max(1, MinItemWidth)));
        double cellW = Math.Max(1, w / cols);
        double cellH = Math.Max(1, ItemHeight);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(
                (i % cols) * cellW,
                (i / cols) * cellH,
                cellW,
                cellH));
        }
        return finalSize;
    }
}
