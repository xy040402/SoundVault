using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>
/// Ctrl+Alt+S 呼出的快捷搜索窗：
/// 打字过滤 · 回车试听 · 拖出去放进 DAW · Esc 关闭。
/// </summary>
public partial class QuickWindow : Window
{
    public sealed class Item
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string Meta { get; init; } = "";
        public string Ext { get; init; } = "";
    }

    private Func<string, List<Item>>? _search;
    private Action<string>? _play;

    private Point _press;
    private Item? _dragItem;
    private bool _canDrag;

    public QuickWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Box.Focus();
    }

    public void Bind(Func<string, List<Item>> search, Action<string> play)
    {
        _search = search;
        _play = play;
    }

    /// <summary>放到光标所在屏幕的 y+12% 处。</summary>
    public void PlaceAtCursor()
    {
        try
        {
            var pos = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(pos);
            var wa = screen.WorkingArea;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

            Left = wa.Left / dpi + (wa.Width / dpi - Width) / 2;
            Top = wa.Top / dpi + wa.Height / dpi * 0.12;
        }
        catch { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
    }

    public void Refresh()
    {
        var q = Box.Text.Trim();
        var list = _search?.Invoke(q) ?? new List<Item>();
        List.ItemsSource = list;
        if (list.Count > 0) List.SelectedIndex = 0;

        HotKeyTag.Text = string.IsNullOrEmpty(HotKey.ActiveName) ? "热键未注册" : HotKey.ActiveName + " 开 / 关";

        Hint.Text = q.Length == 0
            ? "共 {0} 条 · 打字过滤 · 拖到 DAW 直接用 · 再按一次热键关闭".Replace("{0}", list.Count.ToString())
            : $"{list.Count} 条 · 回车试听 · 拖到 DAW · Esc 关闭";
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    // 标题栏拖动 / 关闭
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide();
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
        if (e.Key == Key.Down) { Move(1); e.Handled = true; return; }
        if (e.Key == Key.Up) { Move(-1); e.Handled = true; return; }
        if (e.Key == Key.Enter) { PlaySelected(); e.Handled = true; }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PlaySelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        int i = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = i;
        List.ScrollIntoView(List.Items[i]);
    }

    private void PlaySelected()
    {
        if (List.SelectedItem is not Item it) return;
        _play?.Invoke(it.Path);
        Hint.Text = "正在试听：" + it.Name + " · 拖到 DAW 可直接用";
    }

    // ── 拖到 DAW / 资源管理器 ───────────────────────────────

    private void List_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var container = ItemsControl.ContainerFromElement(List, src) as ListBoxItem;
        _dragItem = container?.DataContext as Item;
        _press = e.GetPosition(this);
        _canDrag = _dragItem is not null;
    }

    private void List_PreviewMove(object sender, MouseEventArgs e)
    {
        if (!_canDrag || _dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _press.X) < 6 && Math.Abs(p.Y - _press.Y) < 6) return;

        _canDrag = false;
        string path = _dragItem.Path;
        if (string.IsNullOrEmpty(path)) return;

        // 和主窗口一致：FileDrop，DAW / 资源管理器 / 剪辑软件都能接
        var data = new DataObject(DataFormats.FileDrop, new[] { path });

        // 拖的时候自己半透明并取消置顶，否则浮窗挡住 DAW，松手落在浮窗上，DAW 收不到
        bool topmost = Topmost;
        try
        {
            Topmost = false;
            Opacity = 0.35;
            SetClickThrough(true);        // 让鼠标事件穿过浮窗，落到下面的 DAW 上
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
        }
        finally
        {
            SetClickThrough(false);
            Opacity = 1;
            Topmost = topmost;
        }
    }

    private void List_PreviewUp(object sender, MouseButtonEventArgs e) => _canDrag = false;

    // ── 拖拽时让窗口"透明"给鼠标：WS_EX_TRANSPARENT ──────────
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private void SetClickThrough(bool on)
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;

            int style = GetWindowLong(h, GwlExStyle);
            int next = on
                ? style | WsExTransparent | WsExLayered
                : style & ~WsExTransparent;

            if (next != style) SetWindowLong(h, GwlExStyle, next);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>按住窗口空白处可以拖动（搜索框和列表里不拖）。</summary>
    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<TextBox>(src) is not null) return;
        if (FindAncestor<ListBox>(src) is not null) return;

        try { DragMove(); } catch { }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
