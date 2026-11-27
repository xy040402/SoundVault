using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>
/// 列表视图、常用搜索词、排序方向记忆、输入法组字保护。
/// </summary>
public partial class MainWindow
{
    private bool _listMode;

    /// <summary>列表视图名称列的像素宽度（绑定到行模板的列宽）。</summary>
    public static readonly DependencyProperty NameColWidthPxProperty =
        DependencyProperty.Register(nameof(NameColWidthPx), typeof(double), typeof(MainWindow),
            new PropertyMetadata(220.0));

    public double NameColWidthPx
    {
        get => (double)GetValue(NameColWidthPxProperty);
        set => SetValue(NameColWidthPxProperty, value);
    }

    // ── 列表视图 ─────────────────────────────────────────────

    private void ApplyViewMode()
    {
        var itemsPanel = (ItemsPanelTemplate)FindResource(_listMode ? "AssetListPanelTpl" : "AssetGridPanelTpl");
        var itemTemplate = (DataTemplate)FindResource(_listMode ? "AssetRowTpl" : "AssetCardTpl");

        AssetList.ItemsPanel = itemsPanel;
        AssetList.ItemTemplate = itemTemplate;

        ViewText.Text = _listMode ? "\uE8FD" : "\uE8A9";          // 列表 / 网格 图标
        ViewPill.ToolTip = _listMode ? "切换回卡片" : "切换成列表";

        if (_listMode)
        {
            double listW = AssetList.ActualWidth;
            if (listW < 200) listW = Math.Max(400, ActualWidth - SidebarColumn.ActualWidth - 6 - 320 - 30);
            ApplyClampedNameWidth(listW);
        }
    }

    private void View_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _listMode = !_listMode;
        _store.ListView = _listMode;
        _store.Save();
        ApplyViewMode();
    }

    /// <summary>列表行左边的播放圆钮：点一下试听这一条（再点暂停）。</summary>
    private void RowPlay_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not Models.AssetItem a) return;

        if (ReferenceEquals(_pinned, a) && _audio.IsPlaying)
        {
            _pinned = null;
            _audio.Stop(60);
            SetPlaying(null);
            UpdatePlayIcon(false);
            SyncPinFlags();
            return;
        }

        if (!_audio.Play(a.FilePath, loop: true))
        {
            ReportPlayFailure(a);
            return;
        }

        _pinned = a;
        SyncPinFlags();
        SetPlaying(a);
        UpdatePlayIcon(true);
    }

    /// <summary>列表行最右边的心：收藏 / 取消收藏。</summary>
    private void RowFav_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not Models.AssetItem a) return;
        ToggleFavorite(a);
    }

    // ── 列表视图：名称列宽度手柄 ─────────────────────────────

    private bool _gripDrag;
    private double _gripStartX, _gripStartW;

    private void NameColGrip_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 双击：恢复默认宽度
        if (e.ClickCount == 2)
        {
            _store.NameColWidth = 220;
            _store.Save();
            NameColWidthPx = 220;
            return;
        }

        _gripDrag = true;
        _gripStartX = e.GetPosition(this).X;
        _gripStartW = _store.NameColWidth;
        (sender as UIElement)?.CaptureMouse();
    }

    private void NameColGrip_Move(object sender, MouseEventArgs e)
    {
        if (!_gripDrag) return;

        double dx = e.GetPosition(this).X - _gripStartX;

        double w = ClampNameWidth(_gripStartW + dx);
        if (Math.Abs(w - _store.NameColWidth) < 0.5) return;

        _store.NameColWidth = w;
        NameColWidthPx = w;
        ApplyNameWidthToRows(w);

        // 关键：立刻把布局跑完，波形控件宽度和裁剪宽度在同一帧更新，
        // 否则会出现"旧几何 + 新裁剪"→ 右边被一点点吃掉，过一会才跳回完整
        AssetList.UpdateLayout();

        if (Environment.GetEnvironmentVariable("SV_GRIP_LOG") == "1")
        {
            var wv = FindFirstWave(AssetList);
            var c0 = AssetList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            Services.Log.Info("拖手柄", $"name={w:0} row={(c0?.ActualWidth ?? -1):0} wave={(wv?.ActualWidth ?? -1):0} lastBar={(wv?.LastBarRight ?? -1):0}");
        }
    }

    private void NameColGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
        (sender as UIElement)?.ReleaseMouseCapture();
        _store.Save();
    }

    /// <summary>名称列宽的上限：要留够波形的位置（播放钮 27 + 时长 44 + 格式 40 + 星星 92 + 心 22 + 边距 24 + 波形最少 160）。</summary>
    private double ClampNameWidth(double w)
    {
        // 用列表实际宽度算（容器可能是回收的旧容器，宽度是旧的，不能信）
        double rowW = Math.Max(0, AssetList.ActualWidth - 30 - 12);

        const double fixedCols = 27 + 44 + 40 + 92 + 22 + 24;   // 播放钮/时长/格式/星星/心/右边距
        const double rowPadding = 12;                            // 行 Border 的 Padding="6,0"
        const double waveMin = 190;                              // 波形保底

        double maxW = Math.Max(120, rowW - fixedCols - rowPadding - waveMin);
        return Math.Clamp(w, 90, Math.Min(620, maxW));
    }

    /// <summary>列表宽度变了（窗口缩小 / 侧栏变宽）：名称列跟着收，别把波形挤没。</summary>
    private void OnAssetListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_listMode) return;
        ApplyClampedNameWidth(e.NewSize.Width);
    }

    /// <summary>把名称列宽直接写到当前已生成的行上（绕开绑定）。</summary>
    private void ApplyNameWidthToRows(double w)
    {
        for (int i = 0; i < AssetList.Items.Count; i++)
        {
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) continue;

            var grid = FindRowGrid(c);
            if (grid is null || grid.ColumnDefinitions.Count < 2) continue;

            grid.ColumnDefinitions[1].Width = new GridLength(w);
        }
    }

    private static Grid? FindRowGrid(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Grid g && g.ColumnDefinitions.Count >= 7) return g;
            var hit = FindRowGrid(child);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>按给定列表宽度夹一次名称列宽（启动、窗口变化、列表变化都走这里）。</summary>
    private void ApplyClampedNameWidth(double listWidth)
    {
        const double fixedCols = 27 + 44 + 40 + 92 + 22 + 24;
        const double rowPadding = 12;
        const double waveMin = 190;

        double rowW = Math.Max(0, listWidth - 30 - 12);
        double maxW = Math.Max(120, rowW - fixedCols - rowPadding - waveMin);
        NameColWidthPx = Math.Clamp(_store.NameColWidth, 90, Math.Min(620, maxW));
        ApplyNameWidthToRows(NameColWidthPx);
    }

    /// <summary>Tag 上的宽度载体（绑定源：Tag.NameColWidth）。</summary>
    private sealed class NameWidth
    {
        public NameWidth(double w) => NameColWidth = w;
        public double NameColWidth { get; }
    }

    // ── 尺寸联动自测（SV_RESIZE_TEST=1）─────────────────────

    /// <summary>
    /// 把左栏从 244 连续拖到 420，同时记录 AssetList 与第一张卡的实际宽度。
    /// 用来证明"改宽度 → 波形跟着重算"是一条连续曲线，而不是跳一下。
    /// </summary>
    private void RunResizeProbe()
    {
        if (Environment.GetEnvironmentVariable("SV_RESIZE_TEST") != "1") return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        int step = 0;
        timer.Tick += (_, _) =>
        {
            step++;
            double w = 244 + step * 18;
            SidebarColumn.Width = new GridLength(w);

            double listW = AssetList.ActualWidth;
            double cardW = 0;
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement c)
                cardW = c.ActualWidth;

            Log.Info("测试", $"step={step:00} sidebar={w:0} list={listW:0} card={cardW:0}");

            if (step >= 10) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>把第一行手柄的屏幕坐标写进日志（SV_GRIP_LOG=1）。</summary>
    private void LogGripAnchor()
    {
        if (Environment.GetEnvironmentVariable("SV_GRIP_LOG") != "1") return;

        int tries = 0;
        var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        retry.Tick += (_, _) =>
        {
            if (++tries > 25) { retry.Stop(); Services.Log.Info("拖手柄", "首行一直没出来"); return; }

            var c0 = AssetList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (c0 is null || c0.ActualWidth <= 0) return;
            retry.Stop();

            var wv = FindFirstWave(AssetList);

            var origin = c0.PointToScreen(new Point(0, 0));
            Services.Log.Info("拖手柄", $"首行屏幕原点=({origin.X:0},{origin.Y:0}) 宽={c0.ActualWidth:0} 高={c0.ActualHeight:0} 名称宽={_store.NameColWidth:0}");
            if (wv is not null)
            {
                var wo = wv.PointToScreen(new Point(0, 0));
                Services.Log.Info("拖手柄", $"波形屏幕原点=({wo.X:0},{wo.Y:0}) 宽={wv.ActualWidth:0}");
            }
        };
        retry.Start();
    }

    // ── 波形截断自测（SV_WAVE_TEST=1）─────────────────────

    private void RunWaveProbe()
    {
        if (Environment.GetEnvironmentVariable("SV_WAVE_TEST") != "1") return;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        int step = 0;
        timer.Tick += (_, _) =>
        {
            step++;
            double w = ClampNameWidth(step <= 5 ? 220 : 420);   // 前半段窄名字，后半段宽名字（走真实夹取）
            _store.NameColWidth = w;
            NameColWidthPx = w;
        ApplyNameWidthToRows(w);

            var wv = FindFirstWave(AssetList);
            if (wv is not null)
                Services.Log.Info("波形", $"step={step:00} name={w:0} row={(AssetList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement)?.ActualWidth:0} view={wv.ActualWidth:0.0} geo={wv.BarsWidth:0.0} lastBar={wv.LastBarRight:0.0}");

            if (step >= 10) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>在可视化树里找第一个 WaveformView。</summary>
    private static Controls.WaveformView? FindFirstWave(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Controls.WaveformView wv) return wv;
            var hit = FindFirstWave(child);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ── 手柄拖动模拟（SV_GRIP_SIM=1）：不用鼠标，直接走同一条代码路径 ──

    private void RunGripSim()
    {
        if (Environment.GetEnvironmentVariable("SV_GRIP_SIM") != "1") return;

        int step = 0;
        double want = 220;
        int delay = int.TryParse(Environment.GetEnvironmentVariable("SV_GRIP_SIM_DELAY_MS"), out var d) ? d : 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            step++;
            want += 20;

            double w = ClampNameWidth(want);
            _store.NameColWidth = w;
            NameColWidthPx = w;
        ApplyNameWidthToRows(w);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var c0 = AssetList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                var wv = FindFirstWave(AssetList);
                if (c0 is null || wv is null) return;

                double rowW = c0.ActualWidth;
                double wave = wv.ActualWidth;
                double lastBar = wv.LastBarRight;
                double total = 12 + 27 + w + wave + 44 + 40 + 92 + 24;

                string verdict = total <= rowW + 0.6 && lastBar >= wave - 8 ? "OK" : "溢出或柱子没铺满";
                Services.Log.Info("模拟拖手柄",
                    $"step={step:00} want={want:0} name={w:0} row={rowW:0} wave={wave:0} lastBar={lastBar:0} total={total:0} -> {verdict}");
            }), System.Windows.Threading.DispatcherPriority.Background);

            if (step >= 20) timer.Stop();
        };

        var start = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay)) };
        start.Tick += (_, _) => { start.Stop(); timer.Start(); };
        start.Start();
    }

    // ── 常用搜索词 ───────────────────────────────────────────

    private void RefreshQuickTerms()
    {
        QuickTermsBar.ItemsSource = null;
        QuickTermsBar.ItemsSource = _store.QuickTerms;
    }

    private void QuickTerm_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string term) return;
        e.Handled = true;
        SearchBox.Text = term;
        SearchBox.CaretIndex = term.Length;
        ApplyFilter();
    }

    private void QuickTerm_Remove(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string term) return;
        e.Handled = true;

        _store.QuickTerms.Remove(term);
        _store.Save();
        RefreshQuickTerms();
        ShowToast($"已从常用词里去掉「{term}」");
    }

    private void QuickTerm_Add(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_store.QuickTerms.Count >= 12)
        {
            ShowToast("常用词最多 12 个");
            return;
        }

        var input = Dialogs.InputDialog.Ask(this, "加一个常用词", "常用搜索词", "", "加上");
        if (string.IsNullOrWhiteSpace(input)) return;

        var term = input.Trim();
        if (term.Length == 0) return;
        if (_store.QuickTerms.Any(t => t.Equals(term, StringComparison.OrdinalIgnoreCase))) return;

        _store.QuickTerms.Add(term);
        _store.Save();
        RefreshQuickTerms();
    }

    // ── 排序方向记忆 ─────────────────────────────────────────

    private static readonly string[] SortKeys = { "name", "dur", "star", "mru" };

    private void SaveSortDir(int mode, bool desc)
    {
        if (mode < 0 || mode >= SortKeys.Length) return;
        _store.SortDirs[SortKeys[mode]] = desc;
        _store.Save();
    }

    /// <summary>切到某个排序键时，用它自己上次的方向（没记过就用默认：名称升序、其它降序）。</summary>
    private bool RecallSortDir(int mode)
    {
        if (mode < 0 || mode >= SortKeys.Length) return false;
        if (_store.SortDirs.TryGetValue(SortKeys[mode], out var desc)) return desc;
        return mode != 0;      // 名称默认升序，时长/评分/导入默认降序
    }
}
