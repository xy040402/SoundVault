using System.IO;
using System.Text;

namespace SoundVault.Setup;

/// <summary>
/// 安装器/卸载器的日志（独立于主程序，简单同步写）。
/// 文件：%LocalAppData%\SoundVault\logs\setup-yyyyMMdd.log，单文件 2 MB，保留 3 个。
/// 安装失败时用户能从这里看出卡在哪一步。
/// </summary>
public static class SetupLog
{
    private const int Keep = 3;
    private const long MaxBytes = 2L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _file;
    private static bool _failed;
    private static int _suffix = 1;

    public static string Dir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("SV_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(custom)) return custom!;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundVault", "logs");
        }
    }

    public static string? File_Path => _file;

    public static void Start(string mode)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Open();
            Info("启动", $"{mode} 版本={typeof(SetupLog).Assembly.GetName().Version} " +
                        $"参数=[{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");
            Info("环境", $"Windows={Environment.OSVersion.VersionString} / {(Environment.Is64BitOperatingSystem ? "64位" : "32位")} " +
                        $"管理员={(IsElevated() ? "是" : "否")} 目录={Dir}");
        }
        catch
        {
            _failed = true;
        }
    }

    public static void Info(string module, string text) => Write("INFO ", module, text);
    public static void Warn(string module, string text) => Write("WARN ", module, text);
    public static void Error(string module, string text, Exception? ex = null)
        => Write("ERROR", module, ex is null ? text : text + " | " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string module, string text)
    {
        if (_failed) return;
        try
        {
            lock (Gate)
            {
                if (_file is null) return;
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{module}] " +
                           text.Replace("\r", " ").Replace("\n", " ⏎ ");
                using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.WriteLine(line);

                if (fs.Length > MaxBytes) Rotate();
            }
        }
        catch
        {
            _failed = true;
        }
    }

    private static void Open()
    {
        string name = $"setup-{DateTime.Now:yyyyMMdd}{(_suffix > 1 ? "-" + _suffix : "")}.log";
        _file = Path.Combine(Dir, name);
    }

    private static void Rotate()
    {
        _suffix++;
        Open();
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("setup-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            foreach (var f in files.Skip(Keep)) { try { f.Delete(); } catch { } }
        }
        catch { }
    }

    /// <summary>读当前日志最后 N 行（诊断信息里贴给作者看）。</summary>
    public static string Tail(int lines = 40)
    {
        try
        {
            if (_file is null || !File.Exists(_file)) return "（没有日志文件）";
            using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, new UTF8Encoding(false));
            var all = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) is not null) all.Add(line);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - lines)));
        }
        catch (Exception ex) { return "（读日志失败：" + ex.Message + "）"; }
    }

    /// <summary>日志目录里最新的几个文件，诊断信息里列出来。</summary>
    public static string[] RecentFiles(int take = 3)
    {
        try
        {
            return new DirectoryInfo(Dir).GetFiles("setup-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(take)
                .Select(f => f.FullName).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
