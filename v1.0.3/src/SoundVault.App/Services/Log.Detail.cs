using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace SoundVault.App.Services;

/// <summary>
/// 详细日志（本版起）：
/// · 平常只写概括（开始/完成/统计） + 错误 + 慢操作
/// · 逐文件逐环节的详细行进内存"黑匣子"，出错 / 卡住 / 用户导出时才落盘
/// </summary>
public static partial class Log
{
    // ── 黑匣子 ──────────────────────────────────────────────
    private const int RingCapacity = 500;
    private static readonly Queue<string> Ring = new();
    private static readonly object RingGate = new();

    /// <summary>详细日志开关（设置里勾选后，详细行也直接落盘）。</summary>
    public static bool Verbose { get; set; }

    /// <summary>当前正在处理的阶段/文件，看门狗卡住时一起倒出来。</summary>
    private static volatile string _stage = "";
    private static volatile string _file = "";
    private static long _lastBeatTicks = DateTime.UtcNow.Ticks;

    private static Timer? _watchdog;

    /// <summary>开始监控：进度超过 15 秒没动就倒黑匣子。</summary>
    public static void StartWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new Timer(_ => CheckStuck(), null, 5000, 5000);
    }

    public static void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>心跳：每处理完一个文件调一次。</summary>
    public static void Beat(string area, string stage, string file)
    {
        _stage = $"{area}/{stage}";
        _file = file;
        Interlocked.Exchange(ref _lastBeatTicks, DateTime.UtcNow.Ticks);
    }

    private static void CheckStuck()
    {
        try
        {
            var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastBeatTicks));
            if (idle.TotalSeconds < 15) return;
            if (string.IsNullOrEmpty(_file)) return;      // 没在干活

            DumpBlackBox($"看门狗：{_stage} 已经 {idle.TotalSeconds:0} 秒没有新进度，最后处理的是 {Path.GetFileName(_file)}");
            Interlocked.Exchange(ref _lastBeatTicks, DateTime.UtcNow.Ticks);
        }
        catch { }
    }

    // ── 详细行：只进内存 ────────────────────────────────────
    public static void Trace(string area, string message)
    {
        if (!Enabled) return;

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [TRACE] [{area}] {message}";
        lock (RingGate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity) Ring.Dequeue();
        }

        if (Verbose) Write(LogLevel.Debug, area, message);
    }

    /// <summary>把黑匣子倒进正式日志（带原因）。</summary>
    public static void DumpBlackBox(string reason)
    {
        try
        {
            string[] lines;
            lock (RingGate) lines = Ring.ToArray();

            Write(LogLevel.Warn, "黑匣子", $"===== 倒出 {lines.Length} 条详细记录：{reason} =====");
            foreach (var l in lines) Write(LogLevel.Warn, "黑匣子", l);
            Write(LogLevel.Warn, "黑匣子", "===== 结束 =====");
            Flush();

            lock (RingGate) Ring.Clear();
        }
        catch { }
    }

    // ── 概括 / 失败 / 慢 ────────────────────────────────────
    /// <summary>一次操作的开始、完成、统计。</summary>
    public static void Op(string area, string phase, string detail = "")
        => Info(area, string.IsNullOrEmpty(detail) ? phase : $"{phase} {detail}");

    /// <summary>单个文件失败：路径、阶段、原因、耗时。</summary>
    public static void Fail(string area, string file, string stage, string why, long ms = 0)
        => Warn(area, $"fail file=\"{file}\" stage={stage} ms={ms} why=\"{why}\"");

    /// <summary>单个文件慢（>300ms），数量少但信息量大。</summary>
    public static void Slow(string area, string file, string stage, long ms)
        => Warn(area, $"slow file=\"{file}\" stage={stage} ms={ms}");

    /// <summary>用户动作：点入点、打标签、评分…（全部落盘）。</summary>
    public static void Action(string area, string action, string? file = null, string extra = "")
    {
        var sb = new StringBuilder(action);
        if (!string.IsNullOrEmpty(file)) sb.Append($" file=\"{file}\"");
        if (!string.IsNullOrEmpty(extra)) sb.Append(' ').Append(extra);
        Info(area, sb.ToString());
    }

    // ── 按时间窗导出 ────────────────────────────────────────
    public sealed class ExportOptions
    {
        public TimeSpan? Window { get; init; }             // null = 全部
        public bool WarningsOnly { get; init; }
        public string? Note { get; init; }
    }

    public sealed class ExportResult
    {
        public string Path { get; init; } = "";
        public int Lines { get; init; }
        public int Failures { get; init; }
        public long Bytes { get; init; }
    }

    /// <summary>估算：某个时间窗内大概多少行、多大（给导出对话框显示）。</summary>
    public static (int Lines, long Bytes) Estimate(TimeSpan? window, bool warningsOnly)
    {
        try
        {
            Flush();
            var cutoff = window is null ? DateTime.MinValue : DateTime.Now - window.Value;
            int lines = 0;
            long bytes = 0;

            foreach (var f in LogFiles())
            {
                foreach (var raw in ReadLinesShared(f.FullName))
                {
                    if (!Within(raw, cutoff)) continue;
                    if (warningsOnly && !IsWarning(raw)) continue;
                    lines++;
                    bytes += raw.Length + 2;
                }
            }
            return (lines, bytes);
        }
        catch { return (0, 0); }
    }

    public static ExportResult ExportWindowed(string zipPath, ExportOptions options)
    {
        Flush();

        var cutoff = options.Window is null ? DateTime.MinValue : DateTime.Now - options.Window.Value;
        var kept = new List<string>();
        var failures = new List<string>();

        foreach (var f in LogFiles())
        {
            foreach (var raw in ReadLinesShared(f.FullName))
            {
                if (!Within(raw, cutoff)) continue;
                if (options.WarningsOnly && !IsWarning(raw)) continue;

                kept.Add(raw);
                if (IsFailure(raw)) failures.Add(raw);
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(zipPath) ?? ".");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "log.txt", string.Join(Environment.NewLine, kept));
            WriteEntry(zip, "failures.csv", ToFailuresCsv(failures));
            WriteEntry(zip, "system.txt", SystemInfo());
            WriteEntry(zip, "readme.txt",
                $"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"时间窗：{(options.Window is null ? "全部" : options.Window.Value.TotalMinutes + " 分钟")}{Environment.NewLine}" +
                $"只含警告和错误：{(options.WarningsOnly ? "是" : "否")}{Environment.NewLine}" +
                $"行数：{kept.Count}{Environment.NewLine}" +
                $"失败行：{failures.Count}{Environment.NewLine}" +
                (string.IsNullOrEmpty(options.Note) ? "" : $"备注：{options.Note}{Environment.NewLine}"));
        }

        var info = new FileInfo(zipPath);
        return new ExportResult
        {
            Path = zipPath,
            Lines = kept.Count,
            Failures = failures.Count,
            Bytes = info.Exists ? info.Length : 0
        };
    }

    private static IEnumerable<FileInfo> LogFiles()
        => new DirectoryInfo(Dir).GetFiles("app-*.log").OrderBy(f => f.LastWriteTimeUtc);

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        // 允许和正在写的日志共享读取
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) is not null) yield return line;
    }

    private static bool Within(string line, DateTime cutoff)
    {
        if (cutoff == DateTime.MinValue) return true;
        if (line.Length < 23) return false;
        return DateTime.TryParse(line.AsSpan(0, 19), out var t) && t >= cutoff;
    }

    private static bool IsWarning(string line)
        => line.Contains("[WARN ", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal);

    private static bool IsFailure(string line)
        => line.Contains("fail ", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal);

    private static string ToFailuresCsv(List<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time,level,area,file,stage,ms,why");
        foreach (var l in lines)
        {
            var time = l.Length >= 23 ? l[..23] : "";
            var rest = l.Length > 24 ? l[24..] : "";
            string level = Between(rest, "[", "]");
            var afterLevel = rest.Length > 0 ? rest[(rest.IndexOf(']') + 1)..].TrimStart() : "";
            string area = Between(afterLevel, "[", "]");
            var body = afterLevel.Length > 0 ? afterLevel[(afterLevel.IndexOf(']') + 1)..].Trim() : afterLevel;

            string file = Quoted(body, "file=");
            string stage = Word(body, "stage=");
            string ms = Word(body, "ms=");
            string why = Quoted(body, "why=");

            sb.AppendLine(string.Join(',', Csv(time), Csv(level.Trim()), Csv(area.Trim()), Csv(file), Csv(stage), Csv(ms), Csv(why)));
        }
        return sb.ToString();
    }

    private static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static string Between(string s, string open, string close)
    {
        int a = s.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return "";
        int b = s.IndexOf(close, a + 1, StringComparison.Ordinal);
        return b > a ? s[(a + 1)..b] : "";
    }

    private static string Quoted(string s, string key)
    {
        int i = s.IndexOf(key + "\"", StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length + 1;
        int j = s.IndexOf('"', i);
        return j > i ? s[i..j] : "";
    }

    private static string Word(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length;
        int j = i;
        while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
        return s[i..j];
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
