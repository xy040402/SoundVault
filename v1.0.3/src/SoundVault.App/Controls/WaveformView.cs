using System.Windows;
using System.Windows.Media;

namespace SoundVault.App.Controls;

public sealed class WaveData
{
    public float[] Values { get; }
    public WaveData(float[] values) => Values = values;
}

/// <summary>
/// 卡片/列表里的波形。
/// 柱子按**当前宽度**现算（宽度变了立刻重建），不用缩放：
/// 缩放在窄行里会把柱子压到互相重叠，糊成一团。
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveDataSourceProperty = DependencyProperty.Register(
        nameof(WaveDataSource), typeof(WaveData), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadFractionProperty = DependencyProperty.Register(
        nameof(PlayheadFraction), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(
        nameof(PlayheadBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CursorFractionProperty = DependencyProperty.Register(
        nameof(CursorFraction), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public WaveData? WaveDataSource
    {
        get => (WaveData?)GetValue(WaveDataSourceProperty);
        set => SetValue(WaveDataSourceProperty, value);
    }

    public Brush WaveBrush
    {
        get => (Brush)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public double PlayheadFraction
    {
        get => (double)GetValue(PlayheadFractionProperty);
        set => SetValue(PlayheadFractionProperty, value);
    }

    public double CursorFraction
    {
        get => (double)GetValue(CursorFractionProperty);
        set => SetValue(CursorFractionProperty, value);
    }

    public Brush? PlayedBrush
    {
        get => (Brush?)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public Brush PlayheadBrush
    {
        get => (Brush)GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    /// <summary>诊断用。</summary>
    public double BarsWidth => _barsWidth;
    public double LastBarRight { get; private set; } = -1;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth - 6;
        double h = ActualHeight;
        if (w <= 2 || h <= 4) return;

        // 每帧自检：宽度和几何不一致就重建（不依赖尺寸事件是否送到）
        if (Math.Abs(_barsWidth - w) > 0.5 || Math.Abs(_barsHeight - h) > 0.5) _barsSource = null;
        EnsureBars(w, h);
        if (_bars is null) return;

        dc.DrawGeometry(WaveBrush, null, _bars);

        double frac = PlayheadFraction;
        if (frac >= 0 && PlayedBrush is { } played)
        {
            double headX = Math.Clamp(frac, 0, 1) * w;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, headX, h)));
            dc.DrawGeometry(played, null, _bars);
            dc.Pop();
        }

        double cx0 = frac >= 0 ? 3 + Math.Clamp(frac, 0, 1) * w : -1;

        double cursorFrac = CursorFraction;
        if (cursorFrac >= 0)
        {
            double cx = 3 + Math.Clamp(cursorFrac, 0, 1) * w;
            dc.PushOpacity(0.45);
            dc.DrawRectangle(PlayheadBrush, null, new Rect(cx - 0.5, 0, 1, h));
            dc.Pop();
        }

        if (cx0 >= 0)
        {
            dc.DrawRectangle(PlayheadBrush, null, new Rect(cx0 - 0.9, 0, 1.8, h));
            _tri ??= BuildTriangle();
            dc.PushTransform(new TranslateTransform(cx0, 0));
            dc.DrawGeometry(PlayheadBrush, null, _tri);
            dc.Pop();
        }
    }

    private Geometry? _tri;
    private static Geometry BuildTriangle()
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(-4, 0), true, true);
            ctx.LineTo(new Point(4, 0), true, false);
            ctx.LineTo(new Point(0, 5), true, false);
            ctx.LineTo(new Point(-4, 0), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private void EnsureBars(double w, double h)
    {
        var peaks = WaveDataSource?.Values;
        if (peaks is null || peaks.Length == 0)
        {
            _bars = null;
            _barsSource = null;
            return;
        }

        if (ReferenceEquals(_barsSource, peaks)
            && Math.Abs(_barsWidth - w) < 0.01 && Math.Abs(_barsHeight - h) < 0.01)
            return;

        double mid = h / 2;
        double half = mid - 1.5;

        int target = Math.Clamp((int)Math.Round(w / 3.0), 20, peaks.Length);
        double step = w / target;
        double barW = Math.Max(1.0, Math.Min(step * 0.66, 3.2));   // 柱子永远比间距窄，不会糊成一团

        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < target; i++)
            {
                // 按比例覆盖整段：第 i 根柱子对应 [i*len/target, (i+1)*len/target)
                int s0 = (int)((long)i * peaks.Length / target);
                int s1 = (int)((long)(i + 1) * peaks.Length / target);
                if (s1 <= s0) s1 = Math.Min(peaks.Length, s0 + 1);
                float m = 0f;
                for (int k = s0; k < s1; k++) if (peaks[k] > m) m = peaks[k];

                double amp = Math.Max(0.012, m) * half;
                double x = i * step;
                ctx.BeginFigure(new Point(x, mid - amp), true, true);
                ctx.LineTo(new Point(x, mid + amp), true, false);
                ctx.LineTo(new Point(x + barW, mid + amp), true, false);
                ctx.LineTo(new Point(x + barW, mid - amp), true, false);
            }
        }
        geo.Freeze();

        LastBarRight = (target - 1) * step + barW;
        _bars = geo;
        _barsSource = peaks;
        _barsWidth = w;
        _barsHeight = h;
    }

    private Geometry? _bars;
    private float[]? _barsSource;
    private double _barsWidth = -1;
    private double _barsHeight = -1;
}
