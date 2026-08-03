using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using BvdsForWindows.ViewModels;

namespace BvdsForWindows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        Closed += (_, _) => _vm.Shutdown();
        Closing += (_, e) =>
        {
            // 关闭窗口时若有未保存设置，询问是否应用
            if (!_vm.ConfirmSaveSettings()) e.Cancel = true;
        };
        // Toast 内容变化时淡入（kt 版 toast opacity 过渡）
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Toast) && !string.IsNullOrEmpty(_vm.Toast))
            {
                var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                ToastBorder.BeginAnimation(OpacityProperty, anim);
            }
        };
    }
}
