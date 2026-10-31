using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SoundVault.App.Controls;

/// <summary>
/// 固定尺寸卡片的虚拟化换行面板：只创建"可见行 ± 缓冲行"的卡片。
/// 卡片尺寸一致（234×146），所以行列位置可以直接算出来，不需要测量所有子元素。
///
/// 必须放在 ListBox 里用（不是 ItemsControl）——只有 ListBox 的模板里带 ScrollViewer，
/// 本面板才会被挂成 IScrollInfo，滚动范围才正确。详见 MainWindow.xaml 的注释。
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    // 视口上下各多建一屏缓冲行，滚动时才不会露出空白
    private const int BufferRows = 1;

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    // 本次测量新生成的容器：只测量它们，不动已经量好的卡片（滚动时省一大半时间）
    private readonly List<UIElement> _newlyAdded = new();
    private double _lastCellW = -1, _lastCellH = -1;

    /// <summary>ItemsControl 的容器生成器（比 IItemContainerGenerator 多 IndexFromContainer）。</summary>
    private ItemContainerGenerator? Generator => ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator;

    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(234.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>整行模式（列表视图）：列数固定 1，格子宽度 = 视口宽度。</summary>
    public static readonly DependencyProperty FullRowModeProperty = DependencyProperty.Register(
        nameof(FullRowMode), typeof(bool), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(146.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public bool FullRowMode
    {
        get => (bool)GetValue(FullRowModeProperty);
        set => SetValue(FullRowModeProperty, value);
    }

    private double _cellW = 234, _cellH = 162;

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    // ── 布局 ────────────────────────────────────────────────
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        int itemCount = owner?.Items.Count ?? 0;

        double vw = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double vh = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        if (vw <= 0) vw = CellWidth * 4;
        if (vh <= 0) vh = CellHeight * 4;

        double ch = Math.Max(1, CellHeight);

        // 列数按"最小卡宽"算，然后把剩余宽度平分给每列 —— 这样最后一行不会在右边留一条空档，
        // 窗口或左栏一变宽/变窄，卡片会重新撑满（v0.18.5）
        _columns = FullRowMode ? 1 : Math.Max(1, (int)Math.Floor(vw / Math.Max(1, CellWidth)));
        double cw = FullRowMode ? Math.Max(1, vw) : Math.Max(1, vw / _columns);
        _cellW = cw;
        _cellH = ch;

        int rows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)_columns);
        UpdateScrollInfo(new Size(_columns * cw, rows * ch), new Size(vw, vh));

        if (itemCount == 0 || !IsItemsHost)
        {
            CleanupChildren(-1, -1);
            return new Size(vw, vh);
        }

        int firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ch) - BufferRows);
        int lastRow = (int)Math.Ceiling((_offset.Y + vh) / ch) + BufferRows;
        int firstIndex = firstRow * _columns;
        int lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * _columns - 1);
        if (firstIndex > itemCount - 1) firstIndex = Math.Max(0, (itemCount - 1) / _columns * _columns);

        _newlyAdded.Clear();
        GenerateRange(firstIndex, lastIndex);

        bool sizeChanged = Math.Abs(_lastCellW - cw) > 0.01 || Math.Abs(_lastCellH - ch) > 0.01;
        if (sizeChanged)
        {
            _lastCellW = cw;
            _lastCellH = ch;
            foreach (UIElement child in InternalChildren)
                child.Measure(new Size(cw, ch));
        }
        else
        {
            foreach (var child in _newlyAdded)
                child.Measure(new Size(cw, ch));
        }

        return new Size(vw, vh);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsItemsHost) return finalSize;

        double cw = _cellW;
        double ch = _cellH;

        foreach (UIElement child in InternalChildren)
        {
            int index = Generator!.IndexFromContainer(child);
            if (index < 0) continue;
            child.Arrange(new Rect(index % _columns * cw - _offset.X, index / _columns * ch - _offset.Y, cw, ch));
        }
        return finalSize;
    }

    /// <summary>按需生成容器：只保留 [first, last] 区间内的卡片。</summary>
    private void GenerateRange(int firstIndex, int lastIndex)
    {
        CleanupChildren(firstIndex, lastIndex);
        if (lastIndex < firstIndex) return;

        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        if (childIndex < 0) childIndex = 0;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                bool isNew;
                var child = (UIElement)generator.GenerateNext(out isNew);
                if (!isNew) continue;

                if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                else InsertInternalChild(childIndex, child);

                generator.PrepareItemContainer(child);
                _newlyAdded.Add(child);
            }
        }
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            int index = Generator!.IndexFromContainer(child);
            if (index < 0 || index < firstIndex || index > lastIndex)
            {
                var pos = ItemContainerGenerator.GeneratorPositionFromIndex(index);
                ItemContainerGenerator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0)
                {
                    int start = args.Position.Index;
                    if (start >= 0 && start + args.ItemUICount <= InternalChildren.Count)
                        RemoveInternalChildRange(start, args.ItemUICount);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                // 列表整体替换（换文件夹 / 过滤）：容器全部作废，从头排
                RemoveInternalChildRange(0, InternalChildren.Count);
                _lastCellW = -1;
                _lastCellH = -1;
                break;
        }

        InvalidateMeasure();
    }

    // ── IScrollInfo ─────────────────────────────────────────
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        bool changed = Math.Abs(extent.Width - _extent.Width) > 0.5
                    || Math.Abs(extent.Height - _extent.Height) > 0.5
                    || Math.Abs(viewport.Width - _viewport.Width) > 0.5
                    || Math.Abs(viewport.Height - _viewport.Height) > 0.5;

        _extent = extent;
        _viewport = viewport;

        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxY) { _offset.Y = maxY; changed = true; }
        if (_offset.Y < 0) { _offset.Y = 0; changed = true; }

        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - CellHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset.Y + CellHeight / 3);
    public void LineLeft() => SetHorizontalOffset(_offset.X - CellWidth / 3);
    public void LineRight() => SetHorizontalOffset(_offset.X + CellWidth / 3);

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height + CellHeight);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height - CellHeight);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - CellHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + CellHeight);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - CellWidth);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + CellWidth);

    public void SetHorizontalOffset(double offset) => SetVerticalOffset(_offset.Y);

    public void SetVerticalOffset(double offset)
    {
        double maxY = Math.Max(0, _extent.Height - _viewport.Height);
        double y = Math.Clamp(offset, 0, maxY);
        if (Math.Abs(y - _offset.Y) < 0.1) return;

        _offset.Y = y;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        int index = IndexOfVisual(visual as DependencyObject);
        if (index < 0) return rectangle;

        double top = index / _columns * _cellH;
        double bottom = top + _cellH;

        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        return new Rect(index % _columns * _cellW, top, _cellW, _cellH);
    }

    private int IndexOfVisual(DependencyObject? d)
    {
        while (d is not null)
        {
            int i = Generator?.IndexFromContainer(d) ?? -1;
            if (i >= 0) return i;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return -1;
    }
}
