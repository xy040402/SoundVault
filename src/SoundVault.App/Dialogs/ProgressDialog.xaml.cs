using System.Windows;
using System.Windows.Input;

namespace SoundVault.App.Dialogs;

/// <summary>带取消的进度窗（导出用）。非模态：调用方自己 Show/Close，循环里喂进度。</summary>
public partial class ProgressDialog : Window
{
    public bool Cancelled { get; private set; }

    public ProgressDialog()
    {
        InitializeComponent();
    }

    public static ProgressDialog Open(Window? owner, string title)
    {
        var dlg = new ProgressDialog { Owner = owner };
        if (owner is null || !owner.IsLoaded) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.TitleText.Text = title;
        dlg.Show();
        return dlg;
    }

    /// <summary>更新进度（0 到 total）。</summary>
    public void Report(int done, int total, string? detail = null)
    {
        if (!IsLoaded) return;

        CountText.Text = $"{done} / {total}";
        if (detail is not null) DetailText.Text = detail;

        double full = Math.Max(1, ActualWidth - 36 - 36);      // 去掉 padding 和圆角
        Fill.Width = Math.Max(0, full * Math.Clamp((double)done / Math.Max(1, total), 0, 1));
    }

    public void Finish(string message)
    {
        TitleText.Text = message;
        DetailText.Text = "";
        CountText.Text = "";
        CancelBtn.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancelled = true;
        TitleText.Text = "正在取消…";
        CancelBtn.Visibility = Visibility.Collapsed;
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
