using System.Windows;
using System.Windows.Controls;

namespace BvdsForWindows.Views;

public partial class SettingsView : UserControl
{
    /// <summary>并发数超过此值需要确认（PCL 风格：超过阈值弹窗，确认才保留）</summary>
    private const int ConcurrentWarnThreshold = 15;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void ConcurrentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue <= ConcurrentWarnThreshold || e.OldValue > ConcurrentWarnThreshold) return;

        var result = MessageBox.Show(
            $"并发数超过 {ConcurrentWarnThreshold} 会显著占用带宽和磁盘 IO，\n" +
            "可能触发 B站限速，也可能影响系统整体响应。\n\n是否仍要继续？",
            "BVDS 提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            // 拒绝：回退到阈值
            ConcurrentSlider.Value = ConcurrentWarnThreshold;
        }
    }
}
