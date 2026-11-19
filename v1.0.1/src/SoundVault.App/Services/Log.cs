using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;

namespace SoundVault.App.Services;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 结构化日志：后台队列写入，不阻塞界面。
/// 文件：%LocalAppData%\SoundVault\logs\app-yyyyMMdd.log，单文件 5 MB，保留 5 个。
/// 测试钩子：SV_LOG_DIR 改目录、SV_LOG_MAX_KB 改单文件上限。
/// </summary>
public static class Log
{
    private const int KeepFiles = 5;
    private static long _maxBytes = 5L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static StreamWriter? _writer;
    private static Timer? _timer;
    private static int _suffix = 1;
    private static DateTime _day = DateTime.MinValue;
    private static string? _dir;

    public static bool Enabled { get; set; } = true;

    public static string Dir => _dir ??= ResolveDir();

    public static string CurrentFile { get; private set; } = "";

    // ── 生命周期 ───────────────────────────────────────────

    public static void Start()
    {
        try
        {
            var kb = Environment.GetEnvironmentVariable("SV_LOG_MAX_KB");
            if (int.TryParse(kb, out var v) && v > 0) _maxBytes = v * 1024L;

            Directory.CreateDirectory(Dir);
            lock (Gate) { OpenForToday(); }
            _timer = new Timer(_ => Flush(), null, 200, 200);
        }
        catch
        {
            Enabled = false;   // 日志本身出问题，绝不能拖垮程序
        }
    }

    public static void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        Flush();
        lock (Gate) { Close(); }
    }

    // ── 写 ─────────────────────────────────────────────────

    public static void Debug(string module, string message) => Write(LogLevel.Debug, module, message);
    public static void Info(string module, string message) => Write(LogLevel.Info, module, message);
    public static void Warn(string module, string message) => Write(LogLevel.Warn, module, message);
    public static void Error(string module, string message) => Write(LogLevel.Error, module, message);

    public static void Error(string module, string message, Exception ex)
        => Write(LogLevel.Error, module, message + " | " + Describe(ex));

    public static void Warn(string module, string message, Exception ex)
        => Write(LogLevel.Warn, module, message + " | " + Describe(ex));

    /// <summary>把异常压成一行：类型 + 消息 + 第一层调用栈。</summary>
    public static string Describe(Exception ex)
    {
        try
        {
            var frames = (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string where = frames.Length > 0 ? frames[0].Trim() : "";
            string inner = ex.InnerException is null ? "" : " ← " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            return $"{ex.GetType().Name}: {ex.Message}{inner}{(where.Length > 0 ? " @ " + where : "")}";
        }
        catch
        {
            return ex.GetType().Name;
        }
    }

    private static void Write(LogLevel level, string module, string message)
    {
        if (!Enabled) return;
        try
        {
            string body = (message ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Tag(level)}] [{module}] {body}";

            lock (Gate)
            {
                if (Pending.Count > 20000) Pending.Dequeue();   // 极端情况下不无限涨
                Pending.Enqueue(line);
            }

            if (level == LogLevel.Error) Flush();               // 错误立刻落盘
        }
        catch { }
    }

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR"
    };

    // ── 落盘 ───────────────────────────────────────────────

    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                if (_writer is null) OpenForToday();
                if (_writer is null) return;

                // 只在"确实有内容要写"时滚动，避免留下 0 字节的空文件
                if (Pending.Count > 0 && _writer.BaseStream.Length > _maxBytes) Rotate();

                while (Pending.Count > 0) _writer.WriteLine(Pending.Dequeue());
                _writer.Flush();
            }
            catch
            {
                Enabled = false;
            }
        }
    }

    private static void OpenForToday()
    {
        var today = DateTime.Today;

        if (_writer is not null && today != _day)     // 跨天换文件
        {
            Close();
            _suffix = 1;
        }
        if (_writer is not null) return;

        _day = today;
        string name = $"app-{today:yyyyMMdd}{(_suffix > 1 ? "-" + _suffix : "")}.log";
        string path = Path.Combine(Dir, name);

        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
        CurrentFile = path;
    }

    private static void Rotate()
    {
        Close();
        _suffix++;
        OpenForToday();
        Prune();
    }

    private static void Close()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }

    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("app-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
            foreach (var f in files.Skip(KeepFiles))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static string ResolveDir()
    {
        var custom = Environment.GetEnvironmentVariable("SV_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundVault", "logs");
    }

    // ── 系统信息 ───────────────────────────────────────────

    public static string SystemInfo()
    {
        var sb = new StringBuilder();
        try
        {
            var asm = typeof(Log).Assembly;
            var vi = FileVersionInfo.GetVersionInfo(asm.Location);
            sb.AppendLine($"应用版本   : {vi.ProductVersion} ({vi.FileVersion})");
            sb.AppendLine($"构建时间   : {File.GetLastWriteTime(asm.Location):yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Windows    : {Environment.OSVersion.VersionString} / {(Environment.Is64BitOperatingSystem ? "64 位系统" : "32 位系统")}");
            sb.AppendLine($".NET       : {Environment.Version}");
            sb.AppendLine($"处理器     : {Environment.ProcessorCount} 逻辑核");
            sb.AppendLine($"进程       : {(Environment.Is64BitProcess ? "64 位" : "32 位")}");
            sb.AppendLine($"机器名     : {Environment.MachineName}");
            sb.AppendLine($"数据目录   : {AppPaths.DataDir}");
            sb.AppendLine($"数据模式   : {AppPaths.ModeText}");
            sb.AppendLine($"日志目录   : {Dir}");
            sb.AppendLine($"当前日志   : {CurrentFile}");
            sb.AppendLine($"时区       : {TimeZoneInfo.Local.DisplayName}");
            sb.AppendLine($"系统时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                sb.AppendLine($"屏幕       : {SystemParameters.PrimaryScreenWidth:0}×{SystemParameters.PrimaryScreenHeight:0}，" +
                              $"工作区 {SystemParameters.WorkArea.Width:0}×{SystemParameters.WorkArea.Height:0}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            sb.AppendLine("取系统信息失败：" + Describe(ex));
        }
        return sb.ToString();
    }

    // ── 诊断包 ─────────────────────────────────────────────

    /// <summary>把日志 + 系统信息 + 配置打成一个 zip，方便用户发回来。</summary>
    public static string ExportDiagnostics(string zipPath)
    {
        Flush();

        string tmp = Path.Combine(Path.GetTempPath(), "sv-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "system.txt"), SystemInfo(), new UTF8Encoding(false));

            if (File.Exists(AppPaths.StorePath))
                SafeCopy(AppPaths.StorePath, Path.Combine(tmp, "store.json"));

            string crash = Path.Combine(Path.GetTempPath(), "sv-crash.txt");
            if (File.Exists(crash))
                SafeCopy(crash, Path.Combine(tmp, "crash.txt"));

            // 日志正被本进程以写方式打开，不能直接丢给 ZipFile（它会以 FileShare.Read 打开而失败），
            // 先按 FileShare.ReadWrite 复制一份到临时目录再打包。
            var logs = new DirectoryInfo(Dir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(KeepFiles)
                .ToArray();

            string logTmp = Path.Combine(tmp, "logs");
            Directory.CreateDirectory(logTmp);
            foreach (var f in logs)
                SafeCopy(f.FullName, Path.Combine(logTmp, f.Name));

            // 先写临时 zip，成功后再替换目标，避免失败时留下半个文件
            string tmpZip = Path.Combine(tmp, "diagnostics.zip");
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                foreach (var f in new DirectoryInfo(logTmp).GetFiles())
                    zip.CreateEntryFromFile(f.FullName, "logs/" + f.Name, CompressionLevel.Optimal);
                foreach (var f in new DirectoryInfo(tmp).GetFiles("*.txt"))
                    zip.CreateEntryFromFile(f.FullName, f.Name, CompressionLevel.Optimal);
                foreach (var f in new DirectoryInfo(tmp).GetFiles("*.json"))
                    zip.CreateEntryFromFile(f.FullName, f.Name, CompressionLevel.Optimal);
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmpZip, zipPath);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>复制正在被写入的文件（允许读写共享）。</summary>
    private static void SafeCopy(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
