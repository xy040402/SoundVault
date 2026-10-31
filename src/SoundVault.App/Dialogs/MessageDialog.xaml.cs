using System.Windows;
using System.Windows.Input;

namespace SoundVault.App.Dialogs;

/// <summary>与应用视觉统一的提示框，替代系统 MessageBox（浅色、方角、字体不一致）。</summary>
public partial class MessageDialog : Window
{
    private Action? _action;

    public MessageDialog()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) { _action?.Invoke(); Close(); }
        };
    }

    public static void Show(Window? owner, string title, string body, string? actionText = null, Action? action = null)
    {
        var dlg = new MessageDialog { Owner = owner };

        // Owner 未显示时不能设置 Owner，否则抛异常
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        dlg._action = action;

        if (!string.IsNullOrEmpty(actionText))
        {
            dlg.ActionBtn.Visibility = Visibility.Visible;
            dlg.ActionBtn.Content = actionText;                       // 模板里是 ContentPresenter
        }

        dlg.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _action?.Invoke();
        Close();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        _action?.Invoke();
        Close();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
