using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundVault.App.Audio;
using SoundVault.App.Controls;
using SoundVault.App.Dialogs;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>试听与播放控制（拆分出来的，逻辑未改）</summary>
public partial class MainWindow
{
    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 单击只负责选择（含 Ctrl / Shift 多选），不启动固定播放
        _canDrag = false;
    }
    private void SyncPinFlags()
    {
        foreach (var it in _all) it.IsPinned = ReferenceEquals(it, _pinned);
    }
    private void ShowDragGhost(string[] paths)
    {
        HideDragGhost();

        double total = 0;
        foreach (var p in paths)
        {
            var item = _all.FirstOrDefault(x => string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase));
            total += item?.DurationMs ?? 0;
        }

        string text = paths.Length == 1
            ? $"{Path.GetFileName(paths[0])}  ·  {Fmt(total)}"
            : $"{paths.Length} 个音效  ·  共 {Fmt(total)}";

        var box = new Border
        {
            Background = (Brush)FindResource("MenuBg"),
            BorderBrush = (Brush)FindResource("AccentBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextPrimary")
            }
        };

        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = box
        };
        w.Show();
        _dragGhost = w;

        GetCursorPos(out var pt);
        w.Left = pt.x / VisualDpi - 12;
        w.Top = pt.y / VisualDpi + 18;

        _ghostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _ghostTimer.Tick += (_, _) =>
        {
            if (_dragGhost is null) return;
            if (!GetCursorPos(out var p2)) return;
            _dragGhost.Left = p2.x / VisualDpi - 12;
            _dragGhost.Top = p2.y / VisualDpi + 18;
        };
        _ghostTimer.Start();
    }
    private void HideDragGhost()
    {
        _ghostTimer?.Stop();
        _ghostTimer = null;
        try { _dragGhost?.Close(); } catch { }
        _dragGhost = null;
    }
    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_canDrag || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) < 4 && Math.Abs(p.Y - _pressPoint.Y) < 4) return;

        _canDrag = false;
        var paths = _selected.Count > 0
            ? _selected.Select(x => x.FilePath).ToArray()
            : _dragItem is null ? Array.Empty<string>() : new[] { _dragItem.FilePath };
        if (paths.Length == 0) return;

        _pinned = null;
        SyncPinFlags();
        _audio.Stop(30);
        SetPlaying(null);

        // #23：拖出去就算「本工程已用」
        foreach (var path in paths)
        {
            _store.Touch(path, used: true);
            var item = _all.FirstOrDefault(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (item is not null) item.UsedToday = true;
        }
        _store.Save();

        var data = new DataObject(DataFormats.FileDrop, paths);
        ShowDragGhost(paths);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        finally
        {
            HideDragGhost();
        }
    }
    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AssetItem a) return;
        _hoverItem = a;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }
    private void HoverTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverItem is null || _pinned is not null) return;
        if (!_audio.Play(_hoverItem.FilePath, loop: true)) return;
        SetPlaying(_hoverItem);
    }
    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverItem is not null) _hoverItem.CursorPos = -1;
        _hoverItem = null;
        if (_pinned is not null) return;
        if (_scrubbing is not null) return;      // 正在拖动定位，别停
        _audio.Stop(70);
        SetPlaying(null);
        UpdatePlayIcon(false);
    }
    private void PlayPause_Click(object sender, MouseButtonEventArgs e)
    {
        var target = _pinned ?? _selected.LastOrDefault() ?? _view.FirstOrDefault();
        if (target is null) return;

        if (ReferenceEquals(_pinned, target) && _audio.IsPlaying)
        {
            _pinned = null;
            _audio.Stop(60);
            SetPlaying(null);
            UpdatePlayIcon(false);
            SyncPinFlags();
            return;
        }

        _pinned = target;
        _audio.Play(target.FilePath, loop: true);
        SetPlaying(target);
        UpdatePlayIcon(true);
        SyncPinFlags();
    }
    private void SetPlaying(AssetItem? a)
    {
        if (a is not null)                            // #23：记一笔播放历史
        {
            _store.Touch(a.FilePath, used: false);
            a.UsedToday = true;
        }

        foreach (var it in _all) it.IsPlaying = ReferenceEquals(it, a);

        if (!ReferenceEquals(_nowPlaying, a) && _nowPlaying is not null) _nowPlaying.Playhead = -1;
        _nowPlaying = a;

        if (a is null)
        {
            PlayerLabel.Text = "悬停任意音效即可循环试听";
            PlayerScale.ScaleX = 0;
            PlayerTime.Text = "00:00.00 / 00:00.00";
            _lastTime = "";
            StopFrames();
        }
        else
        {
            PlayerLabel.Text = a.DisplayName;
            _lastTime = "";
            StartFrames();
        }
    }
    private void Wave_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AssetItem a) return;
        double w = fe.ActualWidth;
        if (w <= 0) return;

        double frac = Math.Clamp(e.GetPosition(fe).X / w, 0, 1);
        a.CursorPos = frac;

        // 按住左键拖动 = 连续定位（限到 ~30fps，别把解码器打爆）
        if (_scrubbing is null || !ReferenceEquals(_scrubbing, a)) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        double now = _glowClock.Elapsed.TotalMilliseconds;
        if (now - _lastScrubMs < 33) return;
        _lastScrubMs = now;

        SeekTo(a, fe, e.GetPosition(fe).X);
    }
    private void Wave_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AssetItem a) a.CursorPos = -1;
        // 注意：这里不清 _scrubbing —— 拖动时指针常常会短暂离开元素，
        // 清早了会被"鼠标离开卡片就停播"的逻辑打断。松手时 Wave_MouseUp 会清。
    }
    private void Wave_LostCapture(object sender, MouseEventArgs e)
    {
        _scrubbing = null;
        _scrubTarget = null;
    }
    private void Wave_Seek(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AssetItem a) return;
        e.Handled = true;
        _canDrag = false;
        if (a.IsMissing) return;

        _scrubbing = a;
        _scrubTarget = fe;
        _lastScrubMs = -1000;
        try { fe.CaptureMouse(); } catch { }

        SeekTo(a, fe, e.GetPosition(fe).X);
    }
    private void Wave_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_scrubTarget is not null)
        {
            try { _scrubTarget.ReleaseMouseCapture(); } catch { }
        }
        _scrubbing = null;
        _scrubTarget = null;
    }
    private void SeekTo(AssetItem a, FrameworkElement fe, double x)
    {
        double w = fe.ActualWidth;
        if (w <= 0) return;

        double frac = Math.Clamp(x / w, 0, 0.999);
        double dur = a.DurationMs > 0 ? a.DurationMs : _audio.LengthMs;
        if (dur <= 0) return;
        double ms = dur * frac;

        if (ReferenceEquals(_nowPlaying, a) && _audio.IsPlaying)
        {
            _audio.SeekFileMs(ms);
        }
        else
        {
            _pinned = null;
            SyncPinFlags();
            if (!_audio.Play(a.FilePath, loop: true, startMs: ms, endMs: dur)) return;
            SetPlaying(a);
        }

        a.Playhead = frac;
        a.CursorPos = frac;
        PlayerScale.ScaleX = frac;
        _lastTime = "";
        PlayerTime.Text = $"{Fmt(ms)} / {Fmt(dur)}";
    }
    private void UpdatePlayIcon(bool playing) =>
        PlayIcon.Text = playing ? "\uE769" : "\uE768";
}
