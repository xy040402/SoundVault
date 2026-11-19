using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SoundVault.App;

/// <summary>
/// 新手引导：首次启动时按顺序高亮几块区域，讲清"这里是什么、能做什么"。
/// 六步：搜索 → 左栏 → 卡片区 → 右键菜单 → 播放条 → 设置。看完写 store.Onboarded=true。
/// </summary>
public partial class MainWindow
{
    private sealed record TourStep(Func<FrameworkElement?> Target, string Title, string Body);

    private TourStep[] _onboardSteps = Array.Empty<TourStep>();
    private int _onboardIndex;

    private void InitOnboarding()
    {
        _onboardSteps = new[]
        {
            new TourStep(() => SearchBox, "① 搜索栏：整库秒搜",
                "输中文、拼音首字母（bdx 就能搜到「拔刀相向」），或者用过滤语法：\n" +
                "#标签 · star:>=4 · dur:<2s · sr:>48k · ch:2 · type:wav\n" +
                "多个条件用空格连起来就是「并且」。右边「?」有完整说明，「✕」清空。"),

            new TourStep(() => SidebarPanel, "② 左栏：四个分区",
                "快捷入口（全部音效/收藏/最近导入/标签/重复检查/最近播放）\n" +
                "智能文件夹（保存的一组搜索条件）\n" +
                "集合（你自己挑的一组音效，跨文件夹）\n" +
                "库（素材包文件夹，右侧 ⋯ 可以重命名/重扫/移除）"),

            new TourStep(() => ContentArea, "③ 卡片：每张都能直接操作",
                "鼠标悬停 = 立即循环试听；\n" +
                "右上角点星星打分、点爱心收藏；\n" +
                "波形上按住左右拖 = 拖动播放头；\n" +
                "底部「＋ 标签」打标签。右下角「已用」表示今天播过/拖出去过。"),

            new TourStep(() => ContentArea, "④ 右键菜单：所有操作都在这",
                "在卡片上点右键：加入集合 / 收藏 / 评分 / 标签 / 元数据 /\n" +
                "复制文件 / 复制路径 / 在资源管理器中显示 / 批量重命名 / 导出。"),

            new TourStep(() => PlayerBar, "⑤ 播放条",
                "▶ 播放暂停（空格也行）；「循环: 整段/段」切换循环范围；\n" +
                "「归一化」按 LUFS 把音量拉平（只影响试听，不改文件）；\n" +
                "右边可以调音量、选输出设备。"),

            new TourStep(() => SettingsBtn, "⑥ 设置：外观、导入导出、数据",
                "齿轮里可以：切换深浅色 / 改主色与圆角 / 窗口置顶 / 导出命名模板 /\n" +
                "文件名清洗规则 / 注册右键「添加到音匣」/ 重建索引 / 便携模式。\n\n" +
                "完整说明见软件目录下的《使用说明书.md》。"),
        };
    }

    private void MaybeStartOnboarding()
    {
        if (_store.Onboarded) return;
        StartOnboarding();
    }

    /// <summary>从头开始（首次启动或设置里手动重看）。</summary>
    public void StartOnboarding()
    {
        if (_onboardSteps.Length == 0) InitOnboarding();
        _onboardIndex = 0;
        OnboardLayer.Visibility = Visibility.Visible;
        ShowOnboardStep();
    }

    private void ShowOnboardStep()
    {
        if (_onboardIndex < 0 || _onboardIndex >= _onboardSteps.Length) { EndOnboarding(); return; }

        var step = _onboardSteps[_onboardIndex];
        OnboardTitle.Text = step.Title;
        OnboardBody.Text = step.Body;
        OnboardStep.Text = $"{_onboardIndex + 1} / {_onboardSteps.Length}";
        OnboardNextText.Text = _onboardIndex == _onboardSteps.Length - 1 ? "完成" : "下一步";
        OnboardPrev.Visibility = _onboardIndex == 0 ? Visibility.Collapsed : Visibility.Visible;

        LayoutSpotlight(step.Target());
    }

    /// <summary>把目标区域以外的部分压暗，并在目标周围画一圈强调色。</summary>
    private void LayoutSpotlight(FrameworkElement? target)
    {
        SpotCanvas.Children.Clear();

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        Rect spot = new(0, 0, 0, 0);
        if (target is not null && target.IsVisible && target.ActualWidth > 0)
        {
            try
            {
                var p = target.TranslatePoint(new Point(0, 0), this);
                spot = new Rect(p.X - 8, p.Y - 8, target.ActualWidth + 16, target.ActualHeight + 16);
            }
            catch { }
        }

        // 目标无效就整屏压暗，只留说明卡
        if (spot.Width < 8) spot = new Rect(w / 2 - 1, h / 2 - 1, 2, 2);

        spot.Intersect(new Rect(0, 0, w, h));
        if (spot.IsEmpty) spot = new Rect(w / 2 - 1, h / 2 - 1, 2, 2);

        var dim = new SolidColorBrush(Color.FromArgb(0xB4, 0x05, 0x08, 0x0E));
        dim.Freeze();

        void AddDim(double x, double y, double sw, double sh)
        {
            if (sw <= 0 || sh <= 0) return;
            var r = new Rectangle { Fill = dim, Width = sw, Height = sh };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            SpotCanvas.Children.Add(r);
        }

        AddDim(0, 0, w, Math.Max(0, spot.Top));                                  // 上
        AddDim(0, spot.Bottom, w, Math.Max(0, h - spot.Bottom));                 // 下
        AddDim(0, spot.Top, Math.Max(0, spot.Left), Math.Max(0, spot.Height));   // 左
        AddDim(spot.Right, spot.Top, Math.Max(0, w - spot.Right), Math.Max(0, spot.Height)); // 右

        var ring = new Rectangle
        {
            Width = spot.Width,
            Height = spot.Height,
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 2,
            RadiusX = 12,
            RadiusY = 12
        };
        Canvas.SetLeft(ring, spot.Left);
        Canvas.SetTop(ring, spot.Top);
        SpotCanvas.Children.Add(ring);
    }

    private void EndOnboarding()
    {
        OnboardLayer.Visibility = Visibility.Collapsed;
        SpotCanvas.Children.Clear();
        if (_store.Onboarded) return;
        _store.Onboarded = true;
        _store.Save();
    }

    private void OnboardNext_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onboardIndex++;
        if (_onboardIndex >= _onboardSteps.Length) EndOnboarding();
        else ShowOnboardStep();
    }

    private void OnboardPrev_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_onboardIndex == 0) return;
        _onboardIndex--;
        ShowOnboardStep();
    }

    private void OnboardSkip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndOnboarding();
    }
}
