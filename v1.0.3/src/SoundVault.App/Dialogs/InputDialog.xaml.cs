using System.Windows;
using System.Windows.Input;

namespace SoundVault.App.Dialogs;

/// <summary>跟应用视觉一致的单行输入框（新建标签、保存智能文件夹用）。</summary>
public partial class InputDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public InputDialog()
    {
        InitializeComponent();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            else if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        };

        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>返回用户输入（取消或空字符串返回 null）。</summary>
    public static string? Ask(Window? owner, string title, string body = "", string initial = "", string okText = "好")
    {
        var dlg = new InputDialog { Owner = owner };
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TitleText.Text = title;
        dlg.ValueBox.Text = initial;

        if (!string.IsNullOrEmpty(body))
        {
            dlg.BodyText.Text = body;
            dlg.BodyText.Visibility = Visibility.Visible;
        }

        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
