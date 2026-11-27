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

/// <summary>验收钩子与诊断（从 MainWindow 里拆出来的，逻辑没动）。</summary>
public partial class MainWindow
{
    private void RunLufsTestIfRequested()
    {
        var dir = Environment.GetEnvironmentVariable("SV_LUFS_TEST");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var log = Path.Combine(Path.GetTempPath(), "sv-lufs.txt");
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly).OrderBy(x => x))
            {
                var clip = AudioClip.Load(f);
                if (clip is null) { sb.AppendLine($"{Path.GetFileName(f)}: 读取失败"); continue; }
                double lufs = Services.Loudness.IntegratedLufs(clip.Samples, clip.SampleRate, clip.Channels);
                sb.AppendLine($"{Path.GetFileName(f)}: lufs={lufs:0.0} gain={clip.NormalizeGain:0.000} " +
                              $"({20 * Math.Log10(clip.NormalizeGain):0.0} dB) sr={clip.SampleRate} ch={clip.Channels}");
            }
            File.WriteAllText(log, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "LUFS TEST FAILED: " + ex, System.Text.Encoding.UTF8);
        }
    }
    private void RunPackTestIfRequested()
    {
        var dir = Environment.GetEnvironmentVariable("SV_PACK_TEST");
        if (string.IsNullOrEmpty(dir)) return;

        var log = Path.Combine(Path.GetTempPath(), "sv-pack.txt");
        try
        {
            var p = PackScanner.Scan(dir);
            File.WriteAllText(log,
                $"name={p.Name}\nversion={p.Version}\ndisplay={p.DisplayName}\n" +
                $"cover={Path.GetFileName(p.CoverPath)}\nlicenses={p.LicenseFiles.Count}\n" +
                $"incomplete={p.IncompleteCount}\naudio={p.AudioCount}\n\n{p.Summary}\n",
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "PACK TEST FAILED: " + ex, System.Text.Encoding.UTF8);
        }
    }
    private void RunMetaTestIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("SV_META_TEST");
        if (string.IsNullOrEmpty(path)) return;

        var log = Path.Combine(Path.GetTempPath(), "sv-meta.txt");
        try
        {
            var m = MetadataReader.Read(path);
            File.WriteAllText(log,
                $"has={m.HasAnything}\ndesc={m.Description}\norigin={m.Originator}\nref={m.OriginatorRef}\n" +
                $"date={m.OriginationDate} {m.OriginationTime}\ninam={m.InfoName}\nicmt={m.InfoComment}\n" +
                $"project={m.IxmlProject}\nscene={m.IxmlScene}\nnote={m.IxmlNote}\nsummary={m.Summary}\n",
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "META TEST FAILED: " + ex, System.Text.Encoding.UTF8);
        }
    }
    private async Task RunRenameTestIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SV_RENAME_TEST") != "1") return;

        var log = Path.Combine(Path.GetTempPath(), "sv-rename.txt");
        try
        {
            await Task.Delay(400);
            // 直接按索引里的行构造（不依赖侧栏是否已经刷出来）
            var rows = (_db?.FilesUnder(_lib!.RootPath) ?? new List<FileRow>()).Take(3).ToList();
            var items = rows.Select(r => new AssetItem { FilePath = r.Path, DisplayName = ParseDisplayName(r.Path) }).ToList();
            var plan = items.Select((a, i) => (Item: a, NewName: $"R{i + 1:00}_改名测试")).ToList();
            var dbg = new List<string>();
            foreach (var p in plan) dbg.Add($"item={p.Item.FilePath} exists={File.Exists(p.Item.FilePath)} dir={Path.GetDirectoryName(p.Item.FilePath)}");
            var (ok, errs) = ApplyRename(plan);
            dbg.Add("after:");
            foreach (var p in plan) dbg.Add($"  -> {p.Item.FilePath} exists={File.Exists(p.Item.FilePath)}");
            _store.Save();

            File.WriteAllText(log,
                $"ok={ok} errs={errs.Count}\n" + string.Join("\n", errs) + "\n" + string.Join("\n", dbg) + "\n---\n" +
                string.Join("\n", items.Select(a => Path.GetFileName(a.FilePath))),
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "RENAME TEST FAILED: " + ex, System.Text.Encoding.UTF8);
        }
    }
    private async Task RunExportTestIfRequestedAsync()
    {
        var dir = Environment.GetEnvironmentVariable("SV_EXPORT_TEST");
        if (string.IsNullOrEmpty(dir)) return;

        var log = Path.Combine(Path.GetTempPath(), "sv-export.txt");
        try
        {
            Directory.CreateDirectory(dir);
            var folder = _side.FirstOrDefault(f => !f.IsLibrary);
            bool exportAll = Environment.GetEnvironmentVariable("SV_EXPORT_ALL") == "1";
            if (exportAll) LoadAllView();
            else if (folder is not null) OpenFolder(folder);
            await Task.Delay(500);

            var items = _view.ToList();
            var progress = ProgressDialog.Open(this, "验收：导出");
            bool keep = exportAll && items.Select(i => Path.GetDirectoryName(i.FilePath)).Distinct().Count() > 1;
            var tpl = string.IsNullOrWhiteSpace(_store.ExportTemplate) ? ExportNames.DefaultTemplate : _store.ExportTemplate;
            var (count, csv, cancelled) = await ExportCoreAsync(dir, items, keepStructure: keep, tpl, progress);
            progress.Close();
            File.WriteAllText(log, $"folder={folder?.Name}\nexported={count}\ncsv={csv}\ncancelled={cancelled}\ndir={dir}\n",
                              System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "EXPORT TEST FAILED: " + ex, System.Text.Encoding.UTF8);
        }
    }
    private async Task RunListTestIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SV_LIST_TEST") != "1") return;

        var log = Path.Combine(Path.GetTempPath(), "sv-list.txt");
        var sb = new System.Text.StringBuilder();
        try
        {
            LoadAllView();
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(250);

            var sv = AssetScrollViewer;
            var panel = FindDescendant<Controls.VirtualizingWrapPanel>(AssetList);
            sb.AppendLine($"items={_view.Count}");
            sb.AppendLine($"panel={(panel is null ? "null" : "ok")}");
            sb.AppendLine($"scrollViewer={(sv is null ? "null" : "ok")} extent={sv?.ExtentHeight:0.0} viewport={sv?.ViewportHeight:0.0}");
            sb.AppendLine($"canContentScroll={sv?.CanContentScroll} scrollableH={sv?.ScrollableHeight:0.0}");
            if (panel is not null)
                sb.AppendLine($"panel: extent={panel.ExtentHeight:0.0} viewport={panel.ViewportHeight:0.0} owner={(panel.ScrollOwner is null ? "null" : "set")} offset={panel.VerticalOffset:0.0}");

            double vh = Math.Max(80, sv?.ViewportHeight ?? 100);
            double ext = sv?.ExtentHeight ?? 0;
            int steps = 0;

            for (double y = 0; y <= Math.Max(0, ext - vh); y += vh)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                sv?.ScrollToVerticalOffset(y);
                UpdateLayout();
                sw.Stop();
                await Dispatcher.Yield(DispatcherPriority.Background);

                int realized = panel is null ? -1 : VisualTreeHelper.GetChildrenCount(panel);
                sb.AppendLine($"step={steps} y={y:0} layout_ms={sw.Elapsed.TotalMilliseconds:0.00} realized={realized} offset={sv?.VerticalOffset:0}");
                steps++;
                if (steps >= 40) break;
            }
            sb.AppendLine($"steps={steps}");

            // 自动滚动 + 真实帧率（配合 SV_FPS=1）：模拟滚轮连续下滑，统计每秒渲染帧数
            if (Environment.GetEnvironmentVariable("SV_SCROLL_FPS") == "1" && sv is not null)
            {
                try { File.Delete(FpsPath); } catch { }

                var tEnd = DateTime.Now.AddSeconds(5);
                var frameSw = System.Diagnostics.Stopwatch.StartNew();
                int ticks = 0;

                while (DateTime.Now < tEnd)
                {
                    double next = sv.VerticalOffset + 14;
                    if (next > sv.ExtentHeight - sv.ViewportHeight) next = 0;   // 滚到底就回顶部继续
                    sv.ScrollToVerticalOffset(next);
                    UpdateLayout();
                    ticks++;
                    await Task.Delay(16);
                }

                frameSw.Stop();
                sb.AppendLine($"scroll_ticks={ticks} seconds={frameSw.Elapsed.TotalSeconds:0.00} ticks_per_s={ticks / frameSw.Elapsed.TotalSeconds:0.0}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("LIST TEST FAILED: " + ex);
        }

        try { File.WriteAllText(log, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
    }
    private async Task RunOrgTestIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SV_ORG_TEST") != "1") return;

        var log = Path.Combine(Path.GetTempPath(), "sv-org.txt");
        var sb = new System.Text.StringBuilder();
        try
        {
            var first = _lib?.Root?.Children.FirstOrDefault();
            if (first is null) { sb.AppendLine("NO_FOLDER"); }
            else
            {
                OpenFolder(first);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(300);

                var items = _view.Take(3).ToList();
                foreach (var it in items) ToggleTag(it, "武侠");
                if (items.Count > 0) ToggleTag(items[0], "剑");
                if (items.Count > 1) SetRating(items[1], 4);
                if (items.Count > 0) ToggleFavorite(items[0]);
                if (items.Count > 2) SetRating(items[2], 2);

                sb.AppendLine($"tagged={items.Count} first_tags={string.Join(",", items[0].Tags)}");

                SearchBox.Text = "#武侠";
                await Task.Delay(400);
                sb.AppendLine($"query '#武侠' => {_view.Count}");

                SearchBox.Text = "#剑";
                await Task.Delay(400);
                sb.AppendLine($"query '#剑' => {_view.Count}");

                SearchBox.Text = "star:>=4";
                await Task.Delay(400);
                sb.AppendLine($"query 'star:>=4' => {_view.Count}");

                SearchBox.Text = "star:>=4 #武侠";
                await Task.Delay(400);
                sb.AppendLine($"query 'star:>=4 #武侠' => {_view.Count}");

                SearchBox.Text = "dur:<1s";
                await Task.Delay(400);
                sb.AppendLine($"query 'dur:<1s' => {_view.Count}");

                _db?.SaveQuery("4 星以上的武侠短音效", "star:>=4 #武侠 dur:<3s");
                RefreshSmartFolders();
        RefreshCollections();
                sb.AppendLine($"smart_folders={_db?.SavedQueries().Count ?? 0}");

                SmartList.SelectedIndex = 0;      // 点第一个智能文件夹，验证"一次点击到达"
                await Task.Delay(600);
                sb.AppendLine($"smart_view_title={ContentTitle.Text} count={_view.Count}");

                // ── 标签页：改名 / 删除 ──
                LoadTagsView();
                await Task.Delay(500);
                sb.AppendLine($"tags_page_rows={(TagPageList.Items.Count)} stat={ContentStat.Text}");

                _db?.RenameTag("剑", "刀剑合一");
                RefreshTagPage();
                await Task.Delay(300);
                sb.AppendLine($"after_rename stat={ContentStat.Text} names={string.Join(",", TagPageList.Items.Cast<TagRow>().Select(r => r.Name))}");

                _db?.DeleteTag("武侠");
                RefreshTagPage();
                await Task.Delay(300);
                sb.AppendLine($"after_delete stat={ContentStat.Text} names={string.Join(",", TagPageList.Items.Cast<TagRow>().Select(r => r.Name))}");

                sb.AppendLine("OK");
            }
        }
        catch (Exception ex) { sb.AppendLine("ORG TEST FAILED: " + ex); }

        try { File.WriteAllText(log, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
    }
    /// <summary>验收钩子：SV_IMPORT_DIR=&lt;文件夹&gt; 时，启动后按"拖入文件夹"那条路导入（select:true）。</summary>
    private async Task RunImportDirTestIfRequestedAsync()
    {
        var dir = Environment.GetEnvironmentVariable("SV_IMPORT_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        Log.Info("验收", "SV_IMPORT_DIR 导入 " + dir);
        await AddLibraryAsync(dir, select: true);
        Log.Info("验收", "SV_IMPORT_DIR 导入返回");
    }
    /// <summary>验收钩子：SV_SEARCH_BENCH=1，每项跑 5 次取最快和平均（单次采样会被 GC 干扰）。</summary>
    private async Task RunSearchBenchIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SV_SEARCH_BENCH") != "1") return;
        if (_lib?.Root is null) { Log.Warn("搜索", "SV_SEARCH_BENCH：没有库"); return; }

        var log = Path.Combine(Path.GetTempPath(), "sv-search.txt");
        var sb = new System.Text.StringBuilder();

        (double Min, double Avg) Measure(Action act, int n = 5)
        {
            var times = new List<double>();
            for (int i = 0; i < n; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                act();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            return (times.Min(), times.Average());
        }

        try
        {
            _suppressSearch = true;

            var first = Measure(() => { _searchMode = true; LoadSearchView(); }, 3);
            sb.AppendLine($"首次进入搜索（读全库+建卡片+预热拼音） 最快 {first.Min:0.0} ms / 平均 {first.Avg:0.0} ms，{_all.Count} 条");

            var cached = Measure(() => { LoadSearchView(); }, 5);
            sb.AppendLine($"再次进入搜索（命中缓存） 最快 {cached.Min:0.0} ms / 平均 {cached.Avg:0.0} ms");

            var t1 = Measure(() => { SearchBox.Text = "拔"; ApplyFilter(); }, 5);
            sb.AppendLine($"输入『拔』（首次全库筛） 最快 {t1.Min:0.0} ms / 平均 {t1.Avg:0.0} ms（{_view.Count} 条）");

            var t2 = Measure(() => { SearchBox.Text = "拔刀"; ApplyFilter(); }, 5);
            sb.AppendLine($"再打成『拔刀』（增量） 最快 {t2.Min:0.0} ms / 平均 {t2.Avg:0.0} ms（{_view.Count} 条）");

            var t3 = Measure(() => { SearchBox.Text = "拨"; ApplyFilter(); }, 5);
            sb.AppendLine($"改成『拨』（不扩展，回全库） 最快 {t3.Min:0.0} ms / 平均 {t3.Avg:0.0} ms（{_view.Count} 条）");

            var t4 = Measure(() => { SearchBox.Text = "bd"; ApplyFilter(); }, 5);
            sb.AppendLine($"拼音『bd』 最快 {t4.Min:0.0} ms / 平均 {t4.Avg:0.0} ms（{_view.Count} 条）");

            var t5 = Measure(() => { SearchBox.Text = "拔刀x"; UpdateSearchAffordances(); }, 5);
            sb.AppendLine($"输入框提示切换 UpdateSearchAffordances 最快 {t5.Min:0.0} ms / 平均 {t5.Avg:0.0} ms");

            SearchBox.Text = "";
            _suppressSearch = false;
            await Task.Yield();
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
        finally
        {
            try { File.WriteAllText(log, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
            Log.Info("搜索", "SV_SEARCH_BENCH 完成 → " + log);
        }
    }
    private void RunPurgeTestIfRequested()
    {
        if (Environment.GetEnvironmentVariable("SV_PURGE_TEST") != "1") return;
        if (_lib is null) { TryWritePurge("NO_LIB"); return; }

        try
        {
            var r = PurgeLibraryData(_lib.RootPath);
            TryWritePurge($"assets={r.Assets} played={r.Played} colItems={r.ColItems} tags={r.Tags}");
        }
        catch (Exception ex) { TryWritePurge("FAILED: " + ex.Message); }
    }

    private static void TryWritePurge(string text)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-purge.txt"), text, System.Text.Encoding.UTF8); } catch { }
    }

    private void DumpDbIfRequested()
    {
        if (Environment.GetEnvironmentVariable("SV_DB_DUMP") != "1" || _db is null) return;

        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine($"schema={_db.DiagSchema()} hasMissingCol={_db.DiagHasMissingColumn()} missingRows={_db.DiagMissingCount()} totalRows={_db.DiagTotalRows()}");
            foreach (var l in _db.DiagLibs()) sb.AppendLine("  lib: " + l);
            var rows = _db.FilesUnder(_lib?.RootPath ?? "");
            sb.AppendLine($"files={rows.Count} missing={rows.Count(r => r.Missing)}");
            foreach (var r in rows) sb.AppendLine($"  missing={r.Missing} path={r.Path}");
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }

        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "sv-db.txt"), sb.ToString(), System.Text.Encoding.UTF8); } catch { }
    }
    private void ProbeFps(object? sender, EventArgs e)
    {
        if (!FpsProbe) return;
        _fpsCount++;

        // 帧间隔才反映"卡不卡"：均值之外记最大间隔和超过 20ms 的帧数
        var stamp = DateTime.Now;
        if (_fpsLast != default(DateTime))
        {
            double gap = (stamp - _fpsLast).TotalMilliseconds;
            _fpsGapSum += gap;
            if (gap > _fpsGapMax) _fpsGapMax = gap;
            if (gap > 20) _fpsSlow++;
        }
        _fpsLast = stamp;

        var span = (stamp - _fpsStart).TotalSeconds;
        if (span < 1) return;

        double avgGap = _fpsCount > 1 ? _fpsGapSum / (_fpsCount - 1) : 0;
        try
        {
            File.AppendAllText(FpsPath,
                $"{(int)(_fpsCount / span)} fps  avg {avgGap:0.0}ms  max {_fpsGapMax:0.0}ms  >20ms {_fpsSlow}  ({_fpsCount} frames / {span:0.00}s)\n");
        }
        catch { }
        _fpsCount = 0;
        _fpsStart = stamp;
        _fpsGapSum = 0;
        _fpsGapMax = 0;
        _fpsSlow = 0;
    }
}
