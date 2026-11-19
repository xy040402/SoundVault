using System.IO;

namespace SoundVault.App.Services;

/// <summary>
/// 数据放在哪里：
/// - 普通模式：`%LocalAppData%\SoundVault`
/// - **便携模式**：exe 旁边有 `data\` 目录（或 `portable.txt` 标记）时，配置和索引都放那儿，整个文件夹拷走就能用
/// </summary>
public static class AppPaths
{
    public static string ExeDir => AppContext.BaseDirectory;

    public static string PortableDir => Path.Combine(ExeDir, "data");

    public static bool Portable =>
        Directory.Exists(PortableDir) || File.Exists(Path.Combine(ExeDir, "portable.txt"));

    public static string DataDir => Portable
        ? PortableDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVault");

    public static string StorePath => Path.Combine(DataDir, "store.json");

    public static string DbPath => Path.Combine(DataDir, "index.db");

    public static string ModeText => Portable ? "便携模式（配置在 exe 旁边的 data 文件夹）" : "普通模式（配置在 %LocalAppData%\\SoundVault）";

    /// <summary>把现有配置搬到 exe 旁边，进入便携模式。返回是否成功。</summary>
    public static bool SwitchToPortable(out string message)
    {
        try
        {
            if (Portable) { message = "已经是便携模式了"; return true; }

            Directory.CreateDirectory(PortableDir);

            var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVault", "store.json");
            var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVault", "index.db");

            if (File.Exists(store)) File.Copy(store, Path.Combine(PortableDir, "store.json"), overwrite: true);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = db + suffix;
                if (File.Exists(src)) File.Copy(src, Path.Combine(PortableDir, "index.db" + suffix), overwrite: true);
            }

            message = "已把配置复制到 " + PortableDir + "，重启后生效。";
            return true;
        }
        catch (Exception ex)
        {
            message = "切换失败：" + ex.Message;
            return false;
        }
    }
}
