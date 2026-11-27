using System;
using System.Drawing;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SoundVault.App.Services;

/// <summary>托盘图标：左键开主窗，右键菜单（打开 / 快捷搜索 / 退出）。</summary>
public sealed class Tray : IDisposable
{
    private WinForms.NotifyIcon? _icon;
    private Window? _owner;

    public void Install(Window owner, Action open, Action quickSearch, Action quit)
    {
        _owner = owner;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Lang.T("打开音匣"), null, (_, _) => open());
        menu.Items.Add(Lang.T("快捷搜索"), null, (_, _) => quickSearch());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Lang.T("退出"), null, (_, _) => quit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "音匣 SoundVault",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
        };
        _icon.DoubleClick += (_, _) => open();

        Log.Info("托盘", "已安装");
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var ic = Icon.ExtractAssociatedIcon(exe);
                if (ic is not null) { Log.Info("托盘", $"图标取自 exe（{ic.Width}×{ic.Height}）"); return ic; }
            }

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        try { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        _icon = null;
        _owner = null;
    }
}
