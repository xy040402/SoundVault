using System;
using System.IO;
using Microsoft.Win32;

namespace SoundVault.App.Services;

/// <summary>开机自启：写 HKCU\...\Run，值名固定，命令是「exe --hidden」。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundVault";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var v = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(v) && v.Contains(ExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled) key.SetValue(ValueName, $"\"{ExePath()}\" --hidden", RegistryValueKind.String);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("自启", "写注册表失败", ex);
        }
    }

    private static string ExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        return Path.Combine(AppContext.BaseDirectory, "音匣.exe");
    }
}
