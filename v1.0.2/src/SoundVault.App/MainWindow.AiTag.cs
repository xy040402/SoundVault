using System.IO;
using System.Windows;
using SoundVault.App.Dialogs;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>P4：本地 AI 识别标签（YAMNet）。批量跑、可取消、可撤销。</summary>
public partial class MainWindow
{
    /// <summary>上一批 AI 加的标签，用于"撤销 AI 标签"。只在本次会话里有效。</summary>
    private readonly List<(string Path, List<string> Added)> _aiTagUndo = new();

    private async Task RunAiTagAsync(List<AssetItem> items)
    {
        if (items.Count == 0) return;

        if (!AudioTagger.Available)
        {
            ShowToast("识别模型不可用：" + AudioTagger.Status);
            return;
        }

        var progress = ProgressDialog.Open(this, $"正在识别 {items.Count} 个音效…");
        var added = new List<(string, List<string>)>();
        int done = 0, taggedFiles = 0, skipped = 0;

        try
        {
            foreach (var item in items)
            {
                if (progress.Cancelled) break;

                done++;
                progress.Report(done, items.Count, item.DisplayName);
                await Task.Yield();                      // 让进度条有机会刷新

                List<(string Tag, double Score)> tags;
                try { tags = await Task.Run(() => AudioTagger.Recognize(item.FilePath, topK: 2)); }
                catch (Exception ex) { Log.Warn("识别", "失败 " + item.FilePath, ex); skipped++; continue; }

                // 只保留够有把握的，避免乱打标
                var confident = tags.Where(t => t.Score >= 0.20).Select(t => t.Tag).ToList();
                if (confident.Count == 0) { skipped++; continue; }

                var merged = item.Tags.ToList();
                var newly = new List<string>();
                foreach (var t in confident)
                {
                    if (merged.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) continue;
                    merged.Add(t);
                    newly.Add(t);
                }
                if (newly.Count == 0) { skipped++; continue; }

                try
                {
                    _db?.SetTags(item.FilePath, merged);
                    item.SetTags(merged);
                    added.Add((item.FilePath, newly));
                    taggedFiles++;
                }
                catch (Exception ex)
                {
                    Log.Warn("识别", "写标签失败 " + item.FilePath, ex);
                    skipped++;
                }
            }

            progress.Finish($"识别完成：{taggedFiles} 个加了标签" + (skipped > 0 ? $"，{skipped} 个没认出来" : ""));
            await Task.Delay(300);
        }
        finally
        {
            progress.Close();
        }

        if (added.Count > 0)
        {
            _aiTagUndo.Clear();
            _aiTagUndo.AddRange(added);
            Log.Info("识别", $"AI 打标签 完成 文件={taggedFiles} 跳过={skipped} 取消={progress.Cancelled}");
            ShowToast($"AI 识别：{taggedFiles} 个音效加了标签");
            ReloadCurrentView(_viewLibRoot);
        }
        else
        {
            ShowToast("AI 识别：这次没有新增标签");
        }
    }

    /// <summary>撤销上一次 AI 打标签（只删 AI 加的那些，你手动打的保留）。</summary>
    private void UndoAiTags()
    {
        if (_aiTagUndo.Count == 0) { ShowToast("没有可撤销的 AI 标签"); return; }

        int removed = 0;
        foreach (var (path, tags) in _aiTagUndo)
        {
            try
            {
                var row = _all.FirstOrDefault(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
                var current = row?.Tags.ToList() ?? new List<string>();
                if (current.Count == 0) continue;

                var kept = current.Where(t => !tags.Any(a => a.Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
                if (kept.Count == current.Count) continue;

                _db?.SetTags(path, kept);
                row?.SetTags(kept);
                removed++;
            }
            catch (Exception ex) { Log.Warn("识别", "撤销失败 " + path, ex); }
        }

        Log.Info("识别", $"撤销 AI 标签 文件={removed}");
        ShowToast($"已撤销 AI 标签：{removed} 个音效");
        _aiTagUndo.Clear();
        ReloadCurrentView(_viewLibRoot);
    }
}
