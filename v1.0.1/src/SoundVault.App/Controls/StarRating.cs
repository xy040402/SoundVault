using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundVault.App.Controls;

/// <summary>
/// 5 颗可点击的星（评分用）。
/// 用自绘而不是 5 个 TextBlock —— 一张卡只多 1 个视觉元素，滚动布局不会变慢；
/// 鼠标划过时会预览"点下去会是几星"，点第 N 颗就是 N 星。
/// </summary>
public sealed class StarRating : FrameworkElement
{
    private const double StarSize = 11;
    private const double Gap = 1.6;
    private const string FilledGlyph = "\uE735";
    private const string EmptyGlyph = "\uE734";

    private int _hover;          // 0 = 没悬停；1..5 = 预览到第几颗
    private static Typeface? _face;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(StarRating),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledBrushProperty = DependencyProperty.Register(
        nameof(FilledBrush), typeof(Brush), typeof(StarRating),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(StarRating),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>用户点了第几颗星（1..5）。取消评分由调用方决定（再点同一颗）。</summary>
    public static readonly RoutedEvent StarClickedEvent = EventManager.RegisterRoutedEvent(
        "StarClicked", RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<int>), typeof(StarRating));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush FilledBrush
    {
        get => (Brush)GetValue(FilledBrushProperty);
        set => SetValue(FilledBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public event RoutedPropertyChangedEventHandler<int> StarClicked
    {
        add => AddHandler(StarClickedEvent, value);
        remove => RemoveHandler(StarClickedEvent, value);
    }

    public StarRating()
    {
        Cursor = Cursors.Hand;
        ToolTip = "评分：点第几颗就是几星，再点同一颗取消";
    }

    private static Typeface Face => _face ??= new Typeface(new FontFamily("Segoe MDL2 Assets"),
                                                           FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override Size MeasureOverride(Size availableSize) =>
        new(5 * StarSize + 4 * Gap, StarSize + 6);

    protected override void OnRender(DrawingContext dc)
    {
        double h = StarSize + 6;
        // 透明底：让整条星区都可点击（Transparent 画刷可命中，null 不行）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 5 * StarSize + 4 * Gap, h));

        int filled = Math.Max(Value, 0);
        int preview = _hover;

        for (int i = 1; i <= 5; i++)
        {
            bool on = i <= filled;
            bool pre = !on && i <= preview;

            string glyph = on || pre ? FilledGlyph : EmptyGlyph;
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         Face, StarSize, on || pre ? FilledBrush : EmptyBrush,
                                         VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double x = (i - 1) * (StarSize + Gap);
            double y = (h - text.Height) / 2;

            if (pre) dc.PushOpacity(0.45);
            dc.DrawText(text, new Point(x, y));
            if (pre) dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = IndexAt(e.GetPosition(this).X);
        if (idx == _hover) return;
        _hover = idx;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == 0) return;
        _hover = 0;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int idx = IndexAt(e.GetPosition(this).X);
        if (idx < 1) return;

        e.Handled = true;          // 别让卡片把这下当成"选择/拖动"
        RaiseEvent(new RoutedPropertyChangedEventArgs<int>(Value, idx, StarClickedEvent));
    }

    private static int IndexAt(double x)
    {
        if (x < 0) return 0;
        int i = (int)(x / (StarSize + Gap)) + 1;
        return Math.Clamp(i, 1, 5);
    }
}
