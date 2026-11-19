using System.Windows;
using System.Windows.Media;

namespace SoundVault.App.Controls;

/// <summary>波形峰值数据。用类包一层，避免 XAML 编译器处理数组型依赖属性。</summary>
public sealed class WaveData
{
    public float[] Values { get; }
    public WaveData(float[] values) => Values = values;
}

/// <summary>峰值波形 + DAW 式播放头。只画 min/max 包络与一根竖线，单帧成本极低。</summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty WaveDataSourceProperty = DependencyProperty.Register(
        nameof(WaveDataSource), typeof(WaveData), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>播放头位置 0..1；小于 0 表示不显示。</summary>
    public static readonly DependencyProperty PlayheadFractionProperty = DependencyProperty.Register(
        nameof(PlayheadFraction), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(
        nameof(PlayheadBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>鼠标在波形上的位置 0..1；小于 0 表示不显示（hover 游标线）。</summary>
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

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth - 6;
        double h = ActualHeight;
        if (w <= 2 || h <= 4) return;

        EnsureBarGeometry(w, h);

        double frac = PlayheadFraction;
        double headX = -1;
        if (frac >= 0)
        {
            headX = 3 + Math.Clamp(frac, 0, 1) * w;
            var played = PlayedBrush;
            if (played is not null)
                dc.DrawRectangle(played, null, new Rect(0, 0, headX, h));
        }

        if (_bars is not null) dc.DrawGeometry(WaveBrush, null, _bars);

        // hover 游标线：鼠标现在停在哪一刻，点下去就从这里起播
        double cursorFrac = CursorFraction;
        if (cursorFrac >= 0)
        {
            double cx = 3 + Math.Clamp(cursorFrac, 0, 1) * w;
            dc.PushOpacity(0.45);
            dc.DrawRectangle(PlayheadBrush, null, new Rect(cx - 0.5, 0, 1, h));
            dc.Pop();
        }

        if (headX >= 0)
        {
            dc.DrawRectangle(PlayheadBrush, null, new Rect(headX - 0.9, 0, 1.8, h));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(headX - 4, 0), true, true);
                ctx.LineTo(new Point(headX + 4, 0), true, false);
                ctx.LineTo(new Point(headX, 5), true, false);
                ctx.LineTo(new Point(headX - 4, 0), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(PlayheadBrush, null, geo);
        }
    }

    // 波形几何只在"换了文件 / 尺寸变了"时重建；播放头每帧移动不再重排 520 根柱子
    private void EnsureBarGeometry(double w, double h)
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

        double step = w / peaks.Length;
        double mid = h / 2;
        double half = mid - 1.5;
        double barW = Math.Max(0.8, step * 0.62);

        // 峰值 520 个远超卡片像素宽度，逐柱画会重叠成一片。
        // 降采样到"约 1.6px 一根"，并用 Nonzero 填充规则，避免重叠区被奇偶规则挖空。
        int target = Math.Clamp((int)Math.Round(w / 1.6), 24, peaks.Length);
        step = w / target;
        barW = Math.Max(0.9, Math.Min(step * 0.66, 3.0));
        int per = Math.Max(1, peaks.Length / target);

        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < target; i++)
            {
                int s0 = i * per;
                int s1 = Math.Min(peaks.Length, s0 + per);
                float m = 0f;
                for (int k = s0; k < s1; k++) if (peaks[k] > m) m = peaks[k];

                double amp = Math.Max(0.012, m) * half;
                double x = 3 + i * step;
                ctx.BeginFigure(new Point(x, mid - amp), true, true);
                ctx.LineTo(new Point(x, mid + amp), true, false);
                ctx.LineTo(new Point(x + barW, mid + amp), true, false);
                ctx.LineTo(new Point(x + barW, mid - amp), true, false);
            }
        }
        geo.Freeze();

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
