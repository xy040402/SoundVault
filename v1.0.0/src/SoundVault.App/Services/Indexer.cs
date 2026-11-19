using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using SoundVault.App.Audio;

namespace SoundVault.App.Services;

public sealed class ScanProgress
{
    public string Phase { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";
    public bool Finished { get; init; }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
}

public sealed class ScanResult
{
    public string Lib { get; init; } = "";
    public int Total { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 增量扫描：只对"新文件 / 大小或修改时间变了"的文件重算头部与波形，
/// 其余直接沿用索引，所以第二次启动是毫秒级。
/// </summary>
public sealed class Indexer
{
    private readonly IndexDb _db;

    public Indexer(IndexDb db) => _db = db;

    /// <summary>验收钩子：SV_SLOW_SCAN=1 时每个文件额外睡一会儿，方便截"扫描进度"的图。</summary>
    private static readonly bool SlowScan = Environment.GetEnvironmentVariable("SV_SLOW_SCAN") == "1";

    public Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(root, progress, ct), ct);

    private ScanResult Scan(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = Normalize(root);

        // ① 枚举（一次目录遍历，拿到全部路径 + 大小 + 修改时间）
        progress?.Report(new ScanProgress { Phase = "正在枚举文件", Total = 0 });
        var files = new List<(string Path, long Size, long Mtime)>(1024);
        foreach (var f in EnumerateAudio(root))
        {
            if (ct.IsCancellationRequested) return new ScanResult { Lib = root, Cancelled = true };
            files.Add(f);
        }

        // ② 跟索引比对，挑出真正需要读文件的
        var stamps = _db.Stamps(root);
        var work = new List<(string Path, long Size, long Mtime)>();
        foreach (var f in files)
        {
            if (!stamps.TryGetValue(f.Path, out var old) || old.Size != f.Size || old.Mtime != f.Mtime)
                work.Add(f);
        }

        int added = work.Count(w => !stamps.ContainsKey(w.Path));
        int changed = work.Count - added;

        var seen = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) seen.Add(f.Path);

        if (work.Count > 0)
            progress?.Report(new ScanProgress { Phase = "正在读取音频头与波形", Total = work.Count });

        // ③ 并行解析新文件（纯计算，不碰数据库）
        var rows = new ConcurrentBag<FileRow>();
        int done = 0;
        var lastReport = Stopwatch.StartNew();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount)),
            CancellationToken = ct
        };

        try
        {
            Parallel.ForEach(work, options, item =>
            {
                var row = ReadFile(item.Path, item.Size, item.Mtime);
                rows.Add(row);
                if (SlowScan) Thread.Sleep(25);

                int n = Interlocked.Increment(ref done);
                if (lastReport.ElapsedMilliseconds >= 120 || n == work.Count)
                {
                    lastReport.Restart();
                    progress?.Report(new ScanProgress
                    {
                        Phase = "正在读取音频头与波形",
                        Done = n,
                        Total = work.Count,
                        Current = Path.GetFileName(item.Path)
                    });
                }
            });
        }
        catch (OperationCanceledException)
        {
            return new ScanResult { Lib = root, Cancelled = true, Elapsed = sw.Elapsed };
        }

        if (ct.IsCancellationRequested)
            return new ScanResult { Lib = root, Cancelled = true, Elapsed = sw.Elapsed };

        // ④ 一次事务写库 + 清理已经不存在的文件
        progress?.Report(new ScanProgress { Phase = "正在写入索引", Total = 0 });
        _db.ApplyScan(root, rows.ToList(), seen);

        int total = _db.CountUnder(root);
        int removed = Math.Max(0, stamps.Count + added - files.Count);

        var result = new ScanResult
        {
            Lib = root,
            Total = total,
            Added = added,
            Changed = changed,
            Removed = removed,
            Elapsed = sw.Elapsed
        };
        progress?.Report(new ScanProgress { Phase = "完成", Finished = true, Done = result.Total, Total = result.Total });
        return result;
    }

    private static FileRow ReadFile(string path, long size, long mtime)
    {
        var row = new FileRow
        {
            Path = path,
            Dir = Path.GetDirectoryName(path) ?? "",
            Name = Path.GetFileNameWithoutExtension(path),
            Size = size,
            Mtime = mtime
        };

        var info = WavReader.TryRead(path);
        if (info is null)
        {
            row.Ok = false;
            return row;
        }

        row.SampleRate = info.SampleRate;
        row.Channels = info.Channels;
        row.Bits = info.Bits;
        row.DurationMs = info.DurationMs;
        row.Peaks = WavReader.ComputePeaksQuantized(path, info);
        return row;
    }

    /// <summary>递归枚举音频文件；某个子目录没权限就跳过，不整棵树失败。</summary>
    private static IEnumerable<(string, long, long)> EnumerateAudio(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            FileInfo[] files;
            try { files = new DirectoryInfo(dir).GetFiles(); }
            catch { continue; }

            foreach (var f in files)
            {
                if (!AudioFormats.IsAudio(f.Name)) continue;
                long size, mtime;
                try { size = f.Length; mtime = f.LastWriteTimeUtc.Ticks; }
                catch { continue; }
                yield return (f.FullName, size, mtime);
            }

            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch { continue; }

            foreach (var s in subs)
            {
                try
                {
                    if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;  // 别跟符号链接绕圈
                }
                catch { continue; }
                stack.Push(s.FullName);
            }
        }
    }

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }
}
