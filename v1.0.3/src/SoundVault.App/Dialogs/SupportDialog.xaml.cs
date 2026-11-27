using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>赞赏码弹窗：白底大图，缺图片时给提示，不报错。</summary>
public partial class SupportDialog : Window
{
    private readonly string _kind;        // wechat / alipay
    private BitmapSource? _image;

    public SupportDialog(string kind)
    {
        InitializeComponent();
        _kind = kind;

        var caption = kind == "alipay" ? "支付宝赞赏码" : "微信赞赏码";
        TitleText.Text = caption;
        this.Title = caption;

        Load();
        Log.Action("support", "open", null, $"kind={kind} loaded={_image is not null}");

        if (Environment.GetEnvironmentVariable("SV_SUPPORT_PROBE") == "1")
        {
            Loaded += (_, _) =>
            {
                var root = Content as System.Windows.FrameworkElement;
                Log.Info("support", $"probe root={root?.GetType().Name} w={root?.ActualWidth:0} h={root?.ActualHeight:0} " +
                                    $"bg={(root as System.Windows.Controls.Border)?.Background} " +
                                    $"title=\"{TitleText.Text}\" titleW={TitleText.ActualWidth:0} hintW={Hint.ActualWidth:0}");
            };
        }
    }

    private void Load()
    {
        var name = _kind == "alipay" ? "support-alipay.png" : "support-wechat.png";
        _image = TryLoadPack(name) ?? TryLoadDisk(name);

        if (_image is null)
        {
            Placeholder.Text = $"还没放赞赏码图片\n\n把文件名改成 {name}\n放到程序旁边的 Assets 文件夹即可";
            Hint.Text = "放好后重开这个窗口就能看到；不影响软件任何功能。";
            return;
        }

        Code.Source = _image;
        Code.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
    }

    private static BitmapSource? TryLoadPack(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? TryLoadDisk(string name)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
            if (!File.Exists(path)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null)
        {
            MessageBox.Show(this, "还没有赞赏码图片。", "保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "保存赞赏码",
            Filter = "PNG 图片|*.png",
            FileName = _kind == "alipay" ? "支付宝赞赏码.png" : "微信赞赏码.png"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var fs = File.Create(dlg.FileName);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_image));
            enc.Save(fs);
            Log.Action("support", "save", dlg.FileName, $"kind={_kind}");
        }
        catch (Exception ex)
        {
            Log.Error("support", "保存赞赏码失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
