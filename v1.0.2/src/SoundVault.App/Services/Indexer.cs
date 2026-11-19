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
    // 验收钩子：SV_SLOW_SCAN=1 时每个文件睡 25ms；给数字则按毫秒睡（便于观察进度条）
    private static readonly int SlowScanMs = ParseSlowScan();

    private static int ParseSlowScan()
    {
        var v = Environment.GetEnvironmentVariable("SV_SLOW_SCAN");
        if (string.IsNullOrWhiteSpace(v)) return 0;
        if (int.TryParse(v, out var ms) && ms > 0) return ms;
        return v == "1" ? 25 : 0;
    }

    public Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => Scan(root, progress, ct), ct);

    private ScanResult Scan(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = Normalize(root);

        Log.Info("扫描", $"开始 root={root}");

        // ① 枚举（一次目录遍历，拿到全部路径 + 大小 + 修改时间）
        progress?.Report(new ScanProgress { Phase = "正在枚举文件", Total = 0 });
        var files = new List<(string Path, long Size, long Mtime)>(1024);
        var enumTick = Stopwatch.StartNew();
        foreach (var f in EnumerateAudio(root))
        {
            if (ct.IsCancellationRequested)
            {
                Log.Info("扫描", $"枚举阶段取消，已找到 {files.Count} 个文件");
                return new ScanResult { Lib = root, Cancelled = true };
            }
            files.Add(f);

            // P1：枚举也报数，几万个文件时用户能看到数字在涨，而不是"卡死"
            if (enumTick.ElapsedMilliseconds >= 180)
            {
                enumTick.Restart();
                progress?.Report(new ScanProgress
                {
                    Phase = "正在枚举文件",
                    Done = files.Count,
                    Total = 0,
                    Current = Path.GetFileName(f.Item1)
                });
            }
        }
        Log.Info("扫描", $"枚举完成 files={files.Count} 用时={sw.ElapsedMilliseconds}ms");
        progress?.Report(new ScanProgress { Phase = "已找到文件", Done = files.Count, Total = files.Count });

        // ② 跟索引比对，挑出真正需要读文件的
        var stamps = _db.Stamps(root);
        var work = new List<(string Path, long Size, long Mtime)>();
        foreach (var f in files)
        {
            if (!stamps.TryGetValue(f.Path, out var old) || old.Size != f.Size || old.Mtime != f.Mtime)
                work.Add(f);
        }

        progress?.Report(new ScanProgress { Phase = "正在比对索引", Done = files.Count, Total = files.Count });

        int added = work.Count(w => !stamps.ContainsKey(w.Path));
        int changed = work.Count - added;
        Log.Info("扫描", $"比对索引 new={added} changed={changed} skip={files.Count - work.Count}");

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
                long t0 = Stopwatch.GetTimestamp();
                var row = ReadHeader(item.Path, item.Size, item.Mtime);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                rows.Add(row);

                // 慢文件单独记一行，方便抓性能瓶颈（不逐文件写日志，避免日志爆炸）
                if (ms > 2000) Log.Warn("扫描", $"慢文件 {item.Path} 用时={ms:0}ms 大小={item.Size / 1024}KB");

                if (SlowScanMs > 0) Thread.Sleep(SlowScanMs);

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
            Log.Info("扫描", $"解析阶段取消，已完成 {done}/{work.Count}");
            return new ScanResult { Lib = root, Cancelled = true, Elapsed = sw.Elapsed };
        }

        if (ct.IsCancellationRequested)
        {
            Log.Info("扫描", $"解析阶段取消，已完成 {done}/{work.Count}");
            return new ScanResult { Lib = root, Cancelled = true, Elapsed = sw.Elapsed };
        }

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

        int failed = rows.Count(r => !r.Ok);
        Log.Info("扫描", $"完成 total={total} 新增={added} 更新={changed} 移除={removed} 失败={failed} 用时={sw.ElapsedMilliseconds}ms");
        return result;
    }

    /// <summary>
    /// P2 第二阶段：把索引里"还没有波形"的行补上。
    /// 单独跑在后台（低并发），扫描阶段因此可以只读文件头 —— 3000 个文件从分钟级降到秒级。
    /// </summary>
    public Task<int> FillPeaksAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() => FillPeaks(root, progress, ct));

    private int FillPeaks(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        root = Normalize(root);

        int pending = _db.PeaksMissingCount(root);
        if (pending == 0) return 0;

        Log.Info("波形", $"开始补齐 root={root} 待补={pending}");
        var sw = Stopwatch.StartNew();

        int done = 0, ok = 0, failed = 0;
        var lastReport = Stopwatch.StartNew();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(3, Environment.ProcessorCount / 2)),
            CancellationToken = ct
        };

        try
        {
            while (true)
            {
                var batch = _db.PeaksMissing(root, 200);
                if (batch.Count == 0) break;

                var map = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);

                Parallel.ForEach(batch, options, path =>
                {
                    ct.ThrowIfCancellationRequested();
                    var peaks = ComputePeaks(path);
                    map[path] = peaks;

                    int n = Interlocked.Increment(ref done);
                    if (peaks is null) Interlocked.Increment(ref failed); else Interlocked.Increment(ref ok);

                    if (lastReport.ElapsedMilliseconds >= 200 || n == pending)
                    {
                        lastReport.Restart();
                        progress?.Report(new ScanProgress
                        {
                            Phase = "正在补齐波形",
                            Done = n,
                            Total = pending,
                            Current = Path.GetFileName(path)
                        });
                    }
                });

                // 一批写一次库，别一条一个事务
                _db.SetPeaks(map);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("波形", $"补齐取消，已完成 {done}/{pending}");
            return done;
        }

        Log.Info("波形", $"补齐完成 共={pending} 成功={ok} 失败={failed} 用时={sw.ElapsedMilliseconds}ms");
        return done;
    }

    /// <summary>只算波形，不解其它元信息；失败返回 null（卡片上画平线，不影响使用）。</summary>
    private static byte[]? ComputePeaks(string path)
    {
        try
        {
            var info = WavReader.TryRead(path);
            if (info is not null) return WavReader.ComputePeaksQuantized(path, info);

            var kind = AudioProbe.Sniff(path);
            if (kind == AudioProbe.Container.Aiff || kind == AudioProbe.Container.Aifc)
            {
                using var aiff = new AiffReader(path);
                return WavReader.ComputePeaksFromReader(aiff);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("波形", "计算失败 " + path, ex);
        }
        return null;
    }

    private static FileRow ReadHeader(string path, long size, long mtime)
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
            // 不是 RIFF/WAVE（AIFF、RF64、老式封装…）→ 用系统解码器读元信息，至少让卡片有时长
            var kind = AudioProbe.Sniff(path);

            // AIFF 自己算（系统解码器不可靠），其余容器才交给 MediaFoundation
            if ((kind == AudioProbe.Container.Aiff || kind == AudioProbe.Container.Aifc) &&
                AiffReader.TryReadInfo(path, out var ac, out var ar, out var abits, out var ams))
            {
                row.Channels = ac;
                row.SampleRate = ar;
                row.Bits = abits;
                row.DurationMs = ams;
                row.Ok = true;
                Log.Info("解码", $"AIFF 已解析 {path} 时长={ams:0}ms（波形放到后台补）");
                return row;
            }

            if (AudioProbe.TryReadInfo(path, out var sr2, out var ch2, out var bits2, out var ms2))
            {
                row.SampleRate = sr2;
                row.Channels = ch2;
                row.Bits = bits2;
                row.DurationMs = ms2;
                row.Ok = true;
                Log.Info("解码", $"非 WAV 用系统解码器解析 container={AudioProbe.Describe(kind)} {path}");
                return row;
            }

            row.Ok = false;
            Log.Warn("解码", $"解析失败（{AudioProbe.Describe(kind)}）{path}");
            return row;
        }

        row.SampleRate = info.SampleRate;
        row.Channels = info.Channels;
        row.Bits = info.Bits;
        row.DurationMs = info.DurationMs;
        // P2：波形不在扫描阶段算，交给后台 FillPeaksAsync（大库从"分钟级"降到"秒级"）
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
