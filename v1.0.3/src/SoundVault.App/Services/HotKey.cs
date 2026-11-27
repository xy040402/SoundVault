using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoundVault.App.Services;

/// <summary>全局热键：支持自定义组合，未设置时用 Ctrl+Alt+S，被占用自动降级。</summary>
public static class HotKey
{
    private const int WmHotKey = 0x0312;
    private const int Id = 0x5A01;

    public const uint ModAlt = 0x0001;
    public const uint ModCtrl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static HwndSource? _source;
    private static Action? _action;

    public static string ActiveName { get; private set; } = "";

    public static bool Register(Window window, Action onPressed, (uint Mods, uint Vk, string Name)? custom = null)
    {
        Unregister();

        try
        {
            var helper = new WindowInteropHelper(window);
            var handle = helper.Handle;
            if (handle == IntPtr.Zero) handle = helper.EnsureHandle();

            _action = onPressed;
            _source = HwndSource.FromHwnd(handle);
            if (_source is null) return false;

            _source.AddHook(WndProc);

            var candidates = new List<(uint Mods, uint Vk, string Name)>();
            if (custom is { } c && c.Vk != 0) candidates.Add((c.Mods | ModNoRepeat, c.Vk, c.Name));
            candidates.Add((ModCtrl | ModAlt | ModNoRepeat, 0x53, "Ctrl+Alt+S"));
            candidates.Add((ModCtrl | ModShift | ModNoRepeat, 0x53, "Ctrl+Shift+S"));
            candidates.Add((ModCtrl | ModAlt | ModNoRepeat, 0x20, "Ctrl+Alt+Space"));

            foreach (var (mods, vk, name) in candidates)
            {
                if (!RegisterHotKey(handle, Id, mods, vk)) { Log.Info("热键", $"候选失败：{name}（mods={mods} vk={vk}）"); continue; }
                ActiveName = name;
                Log.Info("热键", name + " 注册成功");
                return true;
            }

            Log.Warn("热键", "候选组合都被占用，快捷搜索暂时只能从托盘打开");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("热键", "注册异常", ex);
            return false;
        }
    }

    public static void Unregister()
    {
        try
        {
            _source?.RemoveHook(WndProc);
            if (_source is not null)
            {
                var handle = _source.Handle;
                if (handle != IntPtr.Zero) UnregisterHotKey(handle, Id);
            }
        }
        catch { }
        _source = null;
        _action = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == Id)
        {
            handled = true;
            try { _action?.Invoke(); } catch (Exception ex) { Log.Warn("热键", "回调异常", ex); }
        }
        return IntPtr.Zero;
    }

    /// <summary>把一次按键转成 RegisterHotKey 需要的 (mods, vk, 显示名)。</summary>
    public static (uint Mods, uint Vk, string Name)? FromKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        if (key == System.Windows.Input.Key.None || key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl
            || key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt
            || key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift
            || key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin) return null;

        uint m = 0;
        var names = new List<string>();
        if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) { m |= ModCtrl; names.Add("Ctrl"); }
        if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0) { m |= ModAlt; names.Add("Alt"); }
        if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) { m |= ModShift; names.Add("Shift"); }
        if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0) { m |= ModWin; names.Add("Win"); }
        if (m == 0) return null;

        uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        names.Add(key.ToString());
        return (m, vk, string.Join("+", names));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}