using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SoundVault.App.Services;

namespace SoundVault.App.Controls;

/// <summary>
/// 滚动动画（P7）：把"一次跳到目的地"改成"逐帧逼近目标"。
/// 两侧共用：左侧栏是 ScrollViewer，右侧网格是自写的 IScrollInfo 面板。
/// </summary>
public static class SmoothScroll
{
    /// <summary>是否开启平滑滚动（设置里关掉就整条链路退回直接跳）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>动画时长（毫秒）。0 或关闭 = 直接跳。</summary>
    public static int DurationMs { get; set; } = 140;

    /// <summary>跟随系统「显示动画」设置：系统关了就不做动画。</summary>
    public static bool SystemAllows
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }
    }

    public static bool Active => Enabled && DurationMs > 0 && SystemAllows;
}

/// <summary>
/// 一个滚动的动画状态机。用法：滚轮时 <see cref="ScrollTo"/> 设目标，
/// 每帧回调把 <see cref="Value"/> 应用到真正的滚动偏移上。
/// </summary>
public sealed class Scroller
{
    private readonly Action<double> _apply;
    private readonly Func<double> _max;          // 上限（Extent - Viewport）

    private double _value;                       // 当前显示的偏移
    private double _from;                        // 本次动画起点
    private double _target;                      // 目标偏移
    private double _startMs;
    private bool _running;
    private bool _applying;                      // 正在由我们自己写偏移（用来区分"外部改动"）
    private int _frames;                         // 本次动画跑了几帧（SV_SCROLL_TRACE 用）

    public Scroller(Action<double> apply, Func<double> max)
    {
        _apply = apply;
        _max = max;
    }

    /// <summary>当前偏移（由外部同步进来，比如滚动条被拖动后）。</summary>
    public double Value
    {
        get => _value;
        set { _value = value; if (!_running) _target = value; }
    }

    /// <summary>把目标偏移累加一段（滚轮），并启动/续接动画。</summary>
    public void ScrollBy(double delta)
    {
        double max = Math.Max(0, _max());
        double next = Math.Clamp(TargetOrValue() + delta, 0, max);

        if (!SmoothScroll.Active || Math.Abs(next - _value) < 0.5)
        {
            Stop();
            _value = next;
            _target = next;
            Apply(next);
            return;
        }

        _from = _value;
        _target = next;
        _startMs = Now;

        if (!_running)
        {
            _running = true;
            _frames = 0;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>外部直接把偏移改了（拖滚动条 / 程序滚动）：取消动画，立即跟随。</summary>
    public void SyncFromOutside(double offset)
    {
        if (_applying) return;          // 是我们自己写的，忽略
        Stop();
        _value = offset;
        _target = offset;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private double TargetOrValue() => _running ? _target : _value;

    /// <summary>动画是否正在跑。</summary>
    public bool IsRunning => _running;

    /// <summary>最后一帧写进去的偏移（用来分辨"这次滚动是不是我们自己造成的"）。</summary>
    public double LastApplied { get; private set; } = double.NaN;

    private static double Now =>
        (double)Environment.TickCount64;

    private void OnFrame(object? sender, EventArgs e)
    {
        double duration = Math.Max(1, SmoothScroll.DurationMs);
        double t = Math.Clamp((Now - _startMs) / duration, 0, 1);

        // ease-out cubic：先快后慢
        double eased = 1 - Math.Pow(1 - t, 3);
        double v = _from + (_target - _from) * eased;

        _frames++;
        _value = v;
        Apply(v);

        if (t >= 1)
        {
            _value = _target;
            Apply(_target);
            Stop();

            if (Environment.GetEnvironmentVariable("SV_SCROLL_TRACE") == "1")
                Log.Info("滚动", $"动画结束 帧={_frames} 距离={_target - _from:0}px 设定={SmoothScroll.DurationMs}ms");
        }
    }

    private void Apply(double v)
    {
        _applying = true;
        try { _apply(v); }
        finally { _applying = false; }
        LastApplied = v;
    }
}
