using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundVault.App.Audio;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>
/// 右侧详情栏：选中单个音效时展开，显示规格、评分、收藏、标签。
/// 剪辑软件投递不在这里，方案见 剪辑软件联动-待做方案.md。
/// </summary>
public partial class MainWindow
{
    private AssetItem? _detailItem;
    // 和声仓一致：只标一侧时另一侧留空，取范围时按 0 / 总长补足
    private double? _selInMs, _selOutMs;
    /// <summary>详情栏波形上的播放头位置（点波形 / 播放中都会更新）。I/O 就是按它取点。</summary>
    private double _detailPosMs = -1;
    private bool _detailOpen;

    /// <summary>标签芯片的显示模型（点 × 时要从 DataContext 拿名字）。</summary>
    private sealed record TagChip(string Name);

    private void ShowDetail(AssetItem? item)
    {
        _detailItem = item;

        if (item is null)
        {
            ShowDetailEmpty();
            return;
        }

        DetailName.Text = item.DisplayName;
        DetailPath.Text = item.FilePath;
        DetailPath.ToolTip = item.FilePath;

        DetailWhere.Text = DescribeWhere(item);

        DetailFormat.Text = string.IsNullOrEmpty(item.Extension)
            ? "—"
            : item.Extension.TrimStart('.').ToUpperInvariant();
        DetailDuration.Text = FormatDuration(item.DurationMs);
        DetailRate.Text = item.SampleRate > 0 ? $"{item.SampleRate / 1000.0:0.#} kHz" : "—";
        DetailBits.Text = item.Bits > 0 ? $"{item.Bits} bit" : "—";
        DetailChannels.Text = item.Channels switch { 1 => "单声道", 2 => "立体声", > 2 => $"{item.Channels} 声道", _ => "—" };
        DetailSize.Text = FormatSize(item.FilePath);

        // 选区：换条目一律清空
        _selInMs = _selOutMs = null;
        _detailPosMs = -1;
        DetailWave.Values = item.PeaksBytes is { Length: > 0 } pb ? WavReader.QuantizedToFloats(pb) : Array.Empty<float>();
        DetailWave.DurationMs = item.DurationMs;
        DetailWave.InMs = 0;
        DetailWave.OutMs = 0;
        DetailWave.PlayheadMs = -1;
        DetailPosText.Text = "播放头 --:--";
        UpdateSelText();

        DetailStars.Value = item.Rating;
        UpdateFavButton(item);
        RefreshDetailTags(item);

        DetailWave.InOutChanged -= OnDetailWaveChanged;
        DetailWave.InOutChanged += OnDetailWaveChanged;
        DetailWave.SeekRequested -= OnDetailWaveSeek;
        DetailWave.SeekRequested += OnDetailWaveSeek;

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
        OpenDetail();
    }

    /// <summary>点波形定位（不改选区，不自动播放，和声仓一致）。</summary>
    private void OnDetailWaveSeek(double ms)
    {
        if (_detailItem is null) return;

        _detailPosMs = ms;                 // I / O 取的就是这个位置
        DetailWave.PlayheadMs = ms;
        DetailPosText.Text = "播放头 " + Fmt(ms);

        if (ReferenceEquals(_nowPlaying, _detailItem) && _audio.IsPlaying)
            _audio.SeekFileMs(ms);
        else
            PlayerTime.Text = $"{Fmt(ms)} / {Fmt(_detailItem.DurationMs)}";
    }

    /// <summary>库名 · 文件夹链（最多两级），用户一眼知道这东西在哪。</summary>
    private string DescribeWhere(AssetItem item)
    {
        try
        {
            var lib = _libs.Values
                .Where(l => !string.IsNullOrEmpty(l.RootPath))
                .Where(l => item.FilePath.StartsWith(l.RootPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.RootPath.Length)
                .FirstOrDefault();
            var libName = lib?.Name ?? "（不在任何库）";
            var dir = Path.GetDirectoryName(item.FilePath) ?? "";
            if (lib is not null && !string.IsNullOrEmpty(lib.RootPath))
            {
                var rel = dir.StartsWith(lib.RootPath, StringComparison.OrdinalIgnoreCase)
                    ? dir.Substring(lib.RootPath.Length).TrimStart('\\')
                    : dir;
                if (!string.IsNullOrEmpty(rel)) libName += " · " + rel.Replace('\\', '/');
            }
            return libName;
        }
        catch { return ""; }
    }

    private static string FormatDuration(double ms)
    {
        if (ms <= 0) return "—";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalMinutes >= 1
            ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}"
            : $"{t.Seconds}.{t.Milliseconds / 100:0} s";
    }

    private static string FormatSize(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return len >= 1024 * 1024
                ? $"{len / 1024.0 / 1024.0:0.0} MB"
                : $"{len / 1024.0:0} KB";
        }
        catch { return "—"; }
    }

    private void UpdateFavButton(AssetItem item)
    {
        DetailFavText.Text = item.IsFavorite ? "已收藏" : "收藏";
        DetailFavText.SetResourceReference(TextBlock.ForegroundProperty, item.IsFavorite ? "Accent" : "TextSecondary");
    }

    private void RefreshDetailTags(AssetItem item)
    {
        DetailTagList.ItemsSource = item.Tags.Select(t => new TagChip(t)).ToList();
    }

    private void OpenDetail()
    {
        const double w = 320;      // 定宽：拖分隔条会连累波形重排，干脆固定
        DetailColumn.MinWidth = Math.Min(240, w);     // 只有开着时才限制最小宽度
        DetailColumn.Width = new GridLength(w);
        DetailSplitColumn.Width = new GridLength(0);       // 不再给分隔条留位置
        DetailSplitter.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        _detailOpen = true;

        if (!_store.DetailVisible)
        {
            _store.DetailVisible = true;              // 用户选了音效 = 想看详情
            _store.Save();
        }
    }

    /// <summary>没有选中：面板留着，显示"选一个音效"，避免右边留一块空白。</summary>
    private void ShowDetailEmpty()
    {
        _detailItem = null;
        _selInMs = _selOutMs = null;

        DetailContent.Visibility = Visibility.Collapsed;
        DetailEmpty.Visibility = Visibility.Visible;

        if (!_detailOpen && _store.DetailVisible) OpenDetail();
    }

    private void CloseDetail(bool save = true)
    {
        if (!_detailOpen) return;

        DetailColumn.MinWidth = 0;                    // 关键：不归零就会留 240px 空白带
        DetailColumn.Width = new GridLength(0);
        DetailSplitColumn.Width = new GridLength(0);
        DetailSplitter.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;
        _detailOpen = false;
        _detailItem = null;

        _store.DetailVisible = false;
        _store.Save();

        // 宽度保留在 store 里，下次选中时还是这个宽度
    }

    private void DetailClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _selection.Clear();
        ApplySelection();      // 先清选中（会切到空态）
        CloseDetail();         // 再真正收起右栏，并记住"用户关了"
    }

    private void DetailSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_detailOpen) return;
        if (DetailColumn.ActualWidth >= 240)
        {
            _store.DetailWidth = DetailColumn.ActualWidth;
            _store.Save();
        }
    }

    private void DetailStars_StarClicked(object sender, RoutedPropertyChangedEventArgs<int> e)
    {
        if (_detailItem is null) return;
        SetRating(_detailItem, e.NewValue);
        DetailStars.Value = _detailItem.Rating;
    }

    private void DetailFav_Click(object sender, MouseButtonEventArgs e)
    {
        if (_detailItem is null) return;
        ToggleFavorite(_detailItem);
        UpdateFavButton(_detailItem);
    }

    private void DetailPlay_Click(object sender, MouseButtonEventArgs e) => PlayDetailSelection();

    private void DetailTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _detailItem is null) return;

        var text = DetailTagBox.Text.Trim();
        if (text.Length == 0) return;

        AddTagToItem(_detailItem, text);
        DetailTagBox.Text = "";
        e.Handled = true;
    }

    private void DetailTagChip_Remove(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_detailItem is null) return;
        if ((sender as FrameworkElement)?.DataContext is not TagChip chip) return;

        var kept = _detailItem.Tags.Where(t => !t.Equals(chip.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        ApplyTags(_detailItem, kept);
    }

    // ── 选区（入点/出点，）────────────────────────────────
    private void OnDetailWaveChanged(double? inMs, double? outMs)
    {
        _selInMs = inMs;
        _selOutMs = outMs;
        DetailWave.InMs = inMs ?? 0;
        DetailWave.OutMs = outMs ?? (_detailItem?.DurationMs ?? 0);
        UpdateSelText();
    }

    private void UpdateSelText()
    {
        double dur = _detailItem?.DurationMs ?? 0;
        (double a, double b) = RangeMs(dur);
        bool has = b - a > 20;

        DetailSelLength.Text = has ? $"{(b - a) / 1000.0:0.00}s" : "整段";
        DetailInText.Text = has ? "入点 " + Fmt(a) : "入点 --:--";
        DetailOutText.Text = has ? "出点 " + Fmt(b) : "出点 --:--";

        // 播放条上同步显示"· 选区 1.6s"
        if (_detailItem is not null && ReferenceEquals(_nowPlaying, _detailItem))
            PlayerLabel.Text = has
                ? $"{_detailItem.DisplayName} · 选区 {(b - a) / 1000.0:0.0}s"
                : _detailItem.DisplayName;
    }

    /// <summary>把详情栏播放头挪到一个位置（卡片波形、详情波形都走这里）。</summary>
    private void SyncDetailPos(double ms)
    {
        if (_detailItem is null) return;
        _detailPosMs = Math.Clamp(ms, 0, _detailItem.DurationMs);
        DetailWave.PlayheadMs = _detailPosMs;
        DetailPosText.Text = "播放头 " + Fmt(_detailPosMs);
    }

    /// <summary>当前选区（未标注的一侧按 0 / 总长补足，和声仓的 range 一致）。</summary>
    private (double In, double Out) RangeMs(double dur)
    {
        double a = _selInMs ?? 0;
        double b = _selOutMs ?? dur;
        if (b < a) (a, b) = (b, a);
        return (a, b);
    }

    private void DetailMarkIn_Click(object sender, MouseButtonEventArgs e)
    {
        if (_detailItem is null) return;

        double pos = CurrentDetailPos();
        _selInMs = pos;
        Log.Action("clip", "in", _detailItem?.FilePath, $"ms={pos}");
        if (_selOutMs is double o && o <= pos) _selOutMs = null;   // 出点跑到了入点前面就丢掉
        OnDetailWaveChanged(_selInMs, _selOutMs);
    }

    /// <summary>I/O 的取点：优先详情栏播放头（点过的位置），再退到播放引擎位置，最后 0。</summary>
    private double CurrentDetailPos()
    {
        if (_detailPosMs >= 0) return Math.Min(_detailPosMs, _detailItem!.DurationMs);
        if (ReferenceEquals(_nowPlaying, _detailItem) && _audio.IsPlaying) return _audio.PositionMs;
        return 0;
    }

    private void DetailMarkOut_Click(object sender, MouseButtonEventArgs e)
    {
        if (_detailItem is null) return;

        double pos = _detailPosMs >= 0 ? Math.Min(_detailPosMs, _detailItem.DurationMs) : _detailItem.DurationMs;
        _selOutMs = pos;
        Log.Action("clip", "out", _detailItem?.FilePath, $"ms={pos}");
        if (_selInMs is double i && i >= pos) _selInMs = null;     // 入点跑到了出点后面就丢掉
        OnDetailWaveChanged(_selInMs, _selOutMs);
    }

    private void DetailClearSel_Click(object sender, MouseButtonEventArgs e)
    {
        _selInMs = _selOutMs = null;
        Log.Action("clip", "clear", _detailItem?.FilePath);
        OnDetailWaveChanged(0, 0);
        if (_detailItem is not null) DetailWave.PlayheadMs = ReferenceEquals(_nowPlaying, _detailItem) ? _audio.PositionMs : -1;
    }

    private void DetailPlaySel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_detailItem is null) return;
        PlayDetailSelection();
    }

    /// <summary>播放当前条目：有选区就循环这一段，没有就整段。</summary>
    private void PlayDetailSelection()
    {
        if (_detailItem is null) return;

        (double a, double b) = RangeMs(_detailItem.DurationMs);
        bool has = b - a > 20;
        bool ok = has
            ? _audio.Play(_detailItem.FilePath, loop: true, startMs: a, endMs: b)
            : _audio.Play(_detailItem.FilePath, loop: true);

        if (!ok) { ReportPlayFailure(_detailItem); return; }

        _pinned = _detailItem;
        SyncPinFlags();
        SetPlaying(_detailItem);
        UpdateSelText();
    }

    /// <summary>条目内跳步：>50 秒每次 5 秒，短的每次 1/10 时长（最小 0.2 秒）。</summary>
    private void SeekDetail(int direction)
    {
        if (_detailItem is null) return;

        double dur = _detailItem.DurationMs;
        if (dur <= 0) return;

        double step = dur > 50_000 ? 5000 : Math.Max(200, dur / 10);
        double cur = ReferenceEquals(_nowPlaying, _detailItem) ? _audio.PositionMs : 0;
        double target = Math.Clamp(cur + direction * step, 0, dur);

        if (ReferenceEquals(_nowPlaying, _detailItem) && _audio.IsPlaying)
        {
            _audio.SeekFileMs(target);
        }
        else
        {
            var ok = _audio.Play(_detailItem.FilePath, loop: true, startMs: target, endMs: dur);
            if (!ok) { ReportPlayFailure(_detailItem); return; }
            SetPlaying(_detailItem);
        }

        DetailWave.PlayheadMs = target;
    }

    /// <summary>给一个音效加标签（去重、写库、刷新芯片）。</summary>
    private void AddTagToItem(AssetItem item, string tag)
    {
        Log.Action("tag", "add", item.FilePath, $"tag=\"{tag}\"");
        tag = tag.Trim();
        if (tag.Length == 0) return;
        if (item.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;

        var merged = item.Tags.ToList();
        merged.Add(tag);
        ApplyTags(item, merged);
    }

    private void ApplyTags(AssetItem item, List<string> tags)
    {
        try
        {
            _db?.SetTags(item.FilePath, tags);
            item.SetTags(tags);
            RefreshDetailTags(item);
            Log.Info("标签", $"{Path.GetFileName(item.FilePath)} → {(tags.Count == 0 ? "（空）" : string.Join("、", tags))}");
            if (_searchMode) ApplyFilter();      // 搜索页里改了标签，结果可能变
        }
        catch (Exception ex)
        {
            Log.Warn("标签", "写标签失败 " + item.FilePath, ex);
            ShowToast("标签没保存成功");
        }
    }
}
