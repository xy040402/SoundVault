using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundVault.App.Controls;

/// <summary>
/// 详情栏里的可选段波形：点击定位、拖动选段、反序自动纠正、
/// 选中的区间用淡绿底 + 两条竖线标出，播放头单独一条线。
/// </summary>
public sealed class SelectableWaveform : FrameworkElement
{
    private const double MinDragPx = 3;

    private float[]? _values;
    private double _durationMs;
    private double _inMs, _outMs;
    private double _playheadMs = -1;

    private bool _dragging;
    private double _dragStartMs;
    private double _savedInMs, _savedOutMs;      // 按下前的选区，没真拖动就恢复

    public event Action<double?, double?>? InOutChanged;   // (inMs, outMs)，null = 这一侧没标
    public event Action<double>? SeekRequested;

    public float[]? Values
    {
        get => _values;
        set { _values = value; _barsSource = null; InvalidateVisual(); }
    }

    public double DurationMs
    {
        get => _durationMs;
        set { _durationMs = Math.Max(0, value); InvalidateVisual(); }
    }

    public double InMs
    {
        get => _inMs;
        set { _inMs = Math.Max(0, value); InvalidateVisual(); }
    }

    public double OutMs
    {
        get => _outMs;
        set { _outMs = Math.Max(0, value); InvalidateVisual(); }
    }

    public double PlayheadMs
    {
        get => _playheadMs;
        set { _playheadMs = value; InvalidateVisual(); }
    }

    public bool HasSelection => _outMs - _inMs > 20;

    public SelectableWaveform()
    {
        Height = 64;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _barsSource = null;          // 换宽度就重算柱子
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 底色
        var bg = TryFindResource("CardBg") as Brush ?? Brushes.Transparent;
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);

        // 选区底色
        if (HasSelection)
        {
            double x0 = MsToX(_inMs, w), x1 = MsToX(_outMs, w);
            var tint = TryFindResource("AccentSoft") as Brush;
            if (tint is not null)
                dc.DrawRectangle(tint, null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
        }

        // 波形柱：播放头之前的用主题色（听过的部分），之后保持灰色
        var idle = TryFindResource("Wave") as Brush ?? Brushes.Gray;
        var played = TryFindResource("Accent") as Brush ?? idle;
        double headX = (_playheadMs >= 0 && _durationMs > 0) ? MsToX(_playheadMs, w) : -1;

        if (EnsureBars(w, h) is { } bars)
        {
            // 先整体画灰，再把播放头之前的部分用主题色盖一层（裁剪只多一次绘制）
            dc.DrawGeometry(idle, null, bars);
            if (headX > 0 && !ReferenceEquals(played, idle))
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, headX, h)));
                dc.DrawGeometry(played, null, bars);
                dc.Pop();
            }
        }

        // 入点 / 出点竖线
        if (HasSelection)
        {
            var line = TryFindResource("Accent") as Brush ?? Brushes.Orange;
            double x0 = MsToX(_inMs, w), x1 = MsToX(_outMs, w);
            dc.DrawRectangle(line, null, new Rect(x0, 0, 1.5, h));
            dc.DrawRectangle(line, null, new Rect(Math.Max(0, x1 - 1.5), 0, 1.5, h));
        }

        // 播放头：主题色的竖线，压在波形上面
        if (headX >= 0)
        {
            var head = TryFindResource("Accent") as Brush ?? Brushes.White;
            dc.DrawRectangle(head, null, new Rect(headX - 1, 0, 2.5, h));
        }
    }

    // 柱子几何只建一次：换文件 / 改尺寸才重建，播放时每帧只是复用
    private Geometry? EnsureBars(double w, double h)
    {
        var peaks = _values;
        if (peaks is null || peaks.Length == 0) return null;

        if (_bars is not null
            && ReferenceEquals(_barsSource, peaks)
            && Math.Abs(_barsWidth - w) < 0.01
            && Math.Abs(_barsHeight - h) < 0.01)
            return _bars;

        int target = Math.Clamp((int)Math.Round(w / 3.0), 20, peaks.Length);
        int per = Math.Max(1, peaks.Length / target);
        double step = w / target;
        double mid = h / 2, half = mid - 3;
        double barW = Math.Max(1.0, Math.Min(step * 0.66, 3.2));

        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < target; i++)
            {
                float m = 0f;
                int s0 = (int)((long)i * peaks.Length / target);
                int s1 = (int)((long)(i + 1) * peaks.Length / target);
                if (s1 <= s0) s1 = Math.Min(peaks.Length, s0 + 1);
                for (int k = s0; k < s1; k++) if (peaks[k] > m) m = peaks[k];
                double amp = Math.Max(0.02, m) * half;
                double x = i * step + 1;
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
        return geo;
    }

    private Geometry? _bars;
    private float[]? _barsSource;
    private double _barsWidth = -1;
    private double _barsHeight = -1;
    private double MsToX(double ms, double w) =>
        _durationMs <= 0 ? 0 : Math.Clamp(ms / _durationMs * w, 0, w);

    private double XToMs(double x, double w) =>
        _durationMs <= 0 || w <= 0 ? 0 : Math.Clamp(x / w, 0, 1) * _durationMs;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_durationMs <= 0) return;

        Focus();
        CaptureMouse();
        _dragging = true;
        _savedInMs = _inMs;
        _savedOutMs = _outMs;
        _dragStartMs = XToMs(e.GetPosition(this).X, ActualWidth);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            double now = XToMs(e.GetPosition(this).X, ActualWidth);
            double lo = Math.Min(_dragStartMs, now), hi = Math.Max(_dragStartMs, now);

            // 还没拖出阈值：只预览播放头，别把已定的入点/出点抹掉
            if (Math.Abs(hi - lo) * ActualWidth / Math.Max(1, _durationMs) < MinDragPx)
            {
                PlayheadMs = now;
                return;
            }

            _inMs = lo;
            _outMs = hi;
            InvalidateVisual();
            return;
        }

        // 鼠标悬停不再画跟随竖线（只有点下去才移动播放头）
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        double end = XToMs(e.GetPosition(this).X, ActualWidth);
        double lo = Math.Min(_dragStartMs, end), hi = Math.Max(_dragStartMs, end);
        _inMs = lo;
        _outMs = hi;

        // 拖得太短 = 单击定位：只移播放头，**不改已有选区**
        bool isClick = Math.Abs(hi - lo) * ActualWidth / Math.Max(1, _durationMs) < MinDragPx;
        if (isClick)
        {
            _inMs = _savedInMs;          // 已定的入点/出点保持不动
            _outMs = _savedOutMs;
            PlayheadMs = lo;
            SeekRequested?.Invoke(lo);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        InvalidateVisual();
        InOutChanged?.Invoke(_inMs, _outMs);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _inMs = _outMs = 0;
        InvalidateVisual();
        InOutChanged?.Invoke(0, 0);
        e.Handled = true;
    }
}
