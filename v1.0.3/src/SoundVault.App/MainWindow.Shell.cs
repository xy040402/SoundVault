using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SoundVault.App.Dialogs;
using SoundVault.App.Models;
using SoundVault.App.Services;

namespace SoundVault.App;

/// <summary>
/// 外壳：托盘常驻、开机自启、全局热键浮窗（全库搜索）、界面语言。
/// </summary>
public partial class MainWindow
{
    private Tray? _tray;
    private QuickWindow? _quick;
    private bool _quitting;

    /// <summary>全库（所有库）缓存：快捷浮窗用它搜索，不跟随当前视图。</summary>
    private List<AssetItem>? _globalAssets;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InitShell();
    }

    private void InitShell()
    {
        try
        {
            Lang.Set(_store.LangCode);
            Autostart.Set(_store.AutostartEnabled);
            ApplyShellSettings();
            RegisterHotKey();

            Closing += (_, e) =>
            {
                if (_quitting || !_store.TrayEnabled) return;
                e.Cancel = true;
                HideToTray();
            };

            if (Environment.GetEnvironmentVariable("SV_SUPPORT_SHOT") is string shotPath && shotPath != "")
            {
                // 离屏渲染赞赏窗，供自动截图核对（不弹窗）
                var d = new SoundVault.App.Dialogs.SupportDialog("wechat");
                d.Measure(new Size(420, 470));
                d.Arrange(new Rect(0, 0, 420, 470));
                d.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(420, 470, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(d);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(shotPath)) enc.Save(fs);
                Log.Info("support", "离屏渲染 → " + shotPath);
            }

            if (Environment.GetEnvironmentVariable("SV_SUPPORT_TEST") is string st && st != "")
                Dispatcher.BeginInvoke(new Action(() =>
                    new SoundVault.App.Dialogs.SupportDialog(st == "2" ? "alipay" : "wechat").Show()), System.Windows.Threading.DispatcherPriority.Background);

            if (Environment.GetEnvironmentVariable("SV_QUICK_TEST") == "1")
                Dispatcher.BeginInvoke(new Action(() => ShowQuickSearch()), System.Windows.Threading.DispatcherPriority.Background);

            Log.Info("外壳", $"托盘={_store.TrayEnabled} 自启={_store.AutostartEnabled} 语言={Lang.Effective} 热键={HotKey.ActiveName}");
        }
        catch (Exception ex)
        {
            Log.Warn("外壳", "初始化失败", ex);
        }
    }

    private void RegisterHotKey()
    {
        var custom = (_store.HotKeyMods, _store.HotKeyVk) is var (m, v) && v != 0 && m != 0
            ? ((uint Mods, uint Vk, string Name)?)((m, v, _store.HotKeyName))
            : null;

        Log.Info("热键", $"store 热键值 mods={_store.HotKeyMods} vk={_store.HotKeyVk} name={_store.HotKeyName}");
        HotKey.Register(this, ShowQuickSearch, custom);
    }

    /// <summary>设置改过之后重新套一遍。</summary>
    private void ApplyShellSettings()
    {
        if (_store.TrayEnabled)
        {
            if (_tray is null)
            {
                _tray = new Tray();
                _tray.Install(this, RestoreFromTray, ShowQuickSearch, QuitApp);
            }
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }

        Lang.Set(_store.LangCode);
        Lang.ApplyTo(this);

        // 空态、卡片这些是稍后才建出来的，等一拍再补套一次
        Dispatcher.BeginInvoke(new Action(() => Lang.ApplyTo(this)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void HideToTray()
    {
        Hide();
        Log.Info("外壳", "收进托盘");
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApp()
    {
        _quitting = true;
        HotKey.Unregister();
        _tray?.Dispose();
        _tray = null;
        _quick?.Close();
        Application.Current.Shutdown();
    }

    // ── 快捷搜索（全库）──────────────────────────────────────

    private void ShowQuickSearch()
    {
        if (_quick is null)
        {
            _quick = new QuickWindow();
            _quick.Bind(QuickSearch, QuickPlay);
        }

        // 热键一次开、再按一次关
        if (_quick.IsVisible)
        {
            _quick.Hide();
            Log.Action("shell", "quick-close");
            return;
        }

        _quick.Refresh();
        _quick.PlaceAtCursor();
        _quick.Show();
        _quick.Activate();
        Log.Action("shell", "quick-open");
        Log.Info("外壳", $"快捷搜索打开：全库 {GlobalAssets().Count} 条");
    }

    /// <summary>所有库的所有音效（懒加载，导入/删库后失效）。</summary>
    private List<AssetItem> GlobalAssets()
    {
        if (_globalAssets is not null) return _globalAssets;

        try
        {
            _globalAssets = _db.FilesAll().Select(CreateAsset).ToList();
            Log.Info("外壳", $"全库缓存建立：{_globalAssets.Count} 条");
        }
        catch (Exception ex)
        {
            Log.Warn("外壳", "全库缓存失败", ex);
            _globalAssets = new List<AssetItem>();
        }
        return _globalAssets;
    }

    /// <summary>导入/删库/改名后调用：下次打开浮窗重建全库缓存。</summary>
    private void InvalidateGlobalAssets() => _globalAssets = null;

    private List<QuickWindow.Item> QuickSearch(string q)
    {
        var list = new List<QuickWindow.Item>();
        try
        {
            var query = SearchQuery.Parse(q);
            foreach (var a in GlobalAssets())
            {
                if (q.Length > 0 && !query.Matches(a)) continue;

                list.Add(new QuickWindow.Item
                {
                    Path = a.FilePath,
                    Name = a.DisplayName,
                    Meta = a.Meta,
                    Ext = a.ExtText
                });

                if (list.Count >= 80) break;
            }
        }
        catch (Exception ex) { Log.Warn("快捷搜索", "查询失败", ex); }
        return list;
    }

    private void QuickPlay(string path)
    {
        try
        {
            _audio.Play(path, loop: true);
            PlayerLabel.Text = System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) { Log.Warn("快捷搜索", "播放失败", ex); }
    }
}
