using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SoundVault.App.Services;

namespace SoundVault.App.Dialogs;

/// <summary>导出日志：选时间窗 → 估算体积 → 打 zip（含 failures.csv）。</summary>
public partial class LogExportDialog : Window
{
    public string? ExportedPath { get; private set; }

    public LogExportDialog()
    {
        InitializeComponent();
        RangeBox.SelectedIndex = 2;      // 默认最近 30 分钟（现场排查最常用）
        RefreshEstimate();
    }

    private TimeSpan? Window()
    {
        if (RangeBox.SelectedItem is not ComboBoxItem it) return TimeSpan.FromMinutes(30);
        var tag = (it.Tag as string) ?? "30";
        return tag switch
        {
            "all" => null,
            "1h" => TimeSpan.FromHours(1),
            "2h" => TimeSpan.FromHours(2),
            "1d" => TimeSpan.FromDays(1),
            _ => int.TryParse(tag, out var min) ? TimeSpan.FromMinutes(min) : TimeSpan.FromMinutes(30)
        };
    }

    private bool WarningsOnlyChecked => WarnOnlyBox.IsChecked == true;

    private void Range_Changed(object sender, SelectionChangedEventArgs e) => RefreshEstimate();

    private void WarnOnly_Changed(object sender, RoutedEventArgs e) => RefreshEstimate();

    private void RefreshEstimate()
    {
        if (EstimateText is null) return;
        var (lines, bytes) = Log.Estimate(Window(), WarningsOnlyChecked);
        EstimateText.Text = lines == 0
            ? "这个时间窗内没有日志"
            : $"预计 {lines:N0} 行 / {FormatSize(bytes)}，压缩后约 {FormatSize(bytes / 8)}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        > 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        > 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出日志",
            Filter = "Zip 压缩包|*.zip",
            FileName = $"音匣日志-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var result = Log.ExportWindowed(dlg.FileName, new Log.ExportOptions
            {
                Window = Window(),
                WarningsOnly = WarningsOnlyChecked
            });
            Log.Action("export", "log-zip", result.Path, $"lines={result.Lines} fails={result.Failures} bytes={result.Bytes}");

            ExportedPath = result.Path;
            MessageBox.Show(this,
                $"已导出：{result.Path}\n\n行数 {result.Lines:N0}，其中失败行 {result.Failures}，压缩包 {result.Bytes / 1024.0:0.0} KB",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("export", "导出日志失败", ex);
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
