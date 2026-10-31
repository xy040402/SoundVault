using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundVault.App.Services;

/// <summary>
/// 资源管理器右键「添加到音匣」：给音频扩展名登记 SystemFileAssociations\shell 命令。
/// 只写 HKCU，不需要管理员权限。
/// </summary>
public static class FileAssociation
{
    private static readonly string[] Exts = { ".wav", ".mp3", ".flac", ".aif", ".aiff", ".ogg", ".m4a" };

    private const string Verb = "SoundVaultAdd";

    /// <summary>当前是否已经登记过（看 .wav 那条）。</summary>
    public static bool IsRegistered => KeyExists(Exts[0]);

    private static string ExePath => Environment.ProcessPath ?? "";

    public static void Register(out string message)
    {
        var exe = ExePath;
        if (exe.Length == 0 || !File.Exists(exe))
        {
            message = "找不到当前 exe 路径，无法登记。";
            return;
        }

        int ok = 0;
        try
        {
            foreach (var ext in Exts)
            {
                using var shell = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}");
                shell.SetValue("", "添加到音匣");
                shell.SetValue("Icon", exe);

                using var cmd = shell.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" --add \"%1\"");
                ok++;
            }
            message = $"已给 {ok} 种音频格式加上右键「添加到音匣」。";
        }
        catch (Exception ex)
        {
            message = "登记失败：" + ex.Message;
        }
    }

    public static void Unregister(out string message)
    {
        int ok = 0;
        try
        {
            foreach (var ext in Exts)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}", throwOnMissingSubKey: false);
                ok++;
            }
            message = $"已移除 {ok} 种格式的右键菜单。";
        }
        catch (Exception ex)
        {
            message = "移除失败：" + ex.Message;
        }
    }

    private static bool KeyExists(string ext)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}");
            return k is not null;
        }
        catch { return false; }
    }

    // ── 单实例之间转交路径（WM_COPYDATA）────────────────────
    public const int WM_COPYDATA = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    /// <summary>把路径发给已经开着的那个窗口。成功返回 true。</summary>
    public static bool SendToRunningInstance(string path)
    {
        var hwnd = FindRunningWindow();
        if (hwnd == IntPtr.Zero) return false;

        var bytes = Encoding.UTF8.GetBytes(path);
        var data = new COPYDATASTRUCT { dwData = (IntPtr)0x5356, cbData = bytes.Length, lpData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, data.lpData, bytes.Length);
            SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref data);
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(data.lpData); }
    }

    public static IntPtr FindRunningWindow()
    {
        IntPtr found = IntPtr.Zero;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                if (p.ProcessName is not ("音匣" or "SoundVault.App")) continue;
                found = p.MainWindowHandle;
                break;
            }
            catch { }
        }
        return found;
    }
}
