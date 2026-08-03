using System.Windows;
using System.Windows.Threading;
using BvdsForWindows.Core;
using BvdsForWindows.Views;
using TaskStatus = BvdsForWindows.Core.TaskStatus;

namespace BvdsForWindows.ViewModels;

/// <summary>主窗口 VM：导航 + 全局服务 + Toast</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public BvdsService Service { get; }
    public Dispatcher Dispatcher { get; }

    private ViewModelBase _currentPage;
    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public HomeViewModel Home { get; }
    public TasksViewModel Tasks { get; }
    public LoginViewModel Login { get; }
    public SettingsViewModel Settings { get; }

    private string _toast = "";
    public string Toast
    {
        get => _toast;
        private set => Set(ref _toast, value);
    }

    public RelayCommand GoHomeCmd { get; }
    public RelayCommand GoTasksCmd { get; }
    public RelayCommand GoLoginCmd { get; }
    public RelayCommand GoSettingsCmd { get; }

    /// <summary>已自动弹过选择窗的任务（去重）</summary>
    private readonly HashSet<string> _promptedTasks = new();
    /// <summary>待弹出的选择窗队列（避免多任务时丢失）</summary>
    private readonly Queue<string> _pendingPrompt = new();
    private bool _dialogOpen;

    public MainViewModel(Dispatcher dispatcher)
    {
        Dispatcher = dispatcher;
        Service = new BvdsService();
        Service.Start();

        Home = new HomeViewModel(Service);
        Tasks = new TasksViewModel(Service);
        Login = new LoginViewModel(Service, ShowToast);
        Settings = new SettingsViewModel(Service);
        _currentPage = Home;

        GoHomeCmd = new RelayCommand(_ => NavigateTo(Home));
        GoTasksCmd = new RelayCommand(_ => NavigateTo(Tasks));
        GoLoginCmd = new RelayCommand(_ => NavigateTo(Login));
        GoSettingsCmd = new RelayCommand(_ => NavigateTo(Settings));

        Service.TasksChanged += () => Dispatcher.BeginInvoke(() =>
        {
            Tasks.Refresh();
            Home.Refresh();
            AutoShowEpisodeSelection();
        });

        // 登录状态变化（成功/退出）时刷新全局登录态（底部 Toast）
        Login.LoginStateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _ = CheckLoginOnStartAsync();
        });

        // 启动时检查登录态
        _ = CheckLoginOnStartAsync();
    }

    /// <summary>导航切换：离开设置页时若有未保存修改则询问</summary>
    private void NavigateTo(ViewModelBase target)
    {
        if (!ConfirmSaveSettings()) return;   // 用户取消：留在设置页
        CurrentPage = target;
    }

    /// <summary>设置未保存时询问是否应用。返回 false = 用户取消当前操作（导航/关闭）</summary>
    public bool ConfirmSaveSettings()
    {
        if (!Settings.IsDirty) return true;
        var result = System.Windows.MessageBox.Show(
            "设置已修改但尚未保存，是否应用？", "BVDS",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);
        if (result == System.Windows.MessageBoxResult.Yes)
            Settings.Save();
        else if (result == System.Windows.MessageBoxResult.No)
            Settings.Discard();
        else
            return false;   // 取消
        return true;
    }

    /// <summary>检测到分P/分集任务时自动弹出多选窗口（每个任务只自动弹一次，多个任务排队逐个弹）</summary>
    private void AutoShowEpisodeSelection()
    {
        // 清理已消失任务的标记
        _promptedTasks.RemoveWhere(id => Service.Scheduler.All.All(t => t.Id != id));

        // 新进入 Selecting 的任务入队（模态期间执行到这里也不会丢失）
        foreach (var task in Service.Scheduler.All)
        {
            if (task.Status == TaskStatus.Selecting &&
                task.PendingPages is { Count: > 0 } &&
                _promptedTasks.Add(task.Id))
            {
                _pendingPrompt.Enqueue(task.Id);
            }
        }

        ShowNextPrompt();
    }

    /// <summary>弹出队列中下一个选择窗</summary>
    private void ShowNextPrompt()
    {
        if (_dialogOpen) return;

        while (_pendingPrompt.Count > 0)
        {
            var id = _pendingPrompt.Dequeue();
            var task = Service.Scheduler.All.FirstOrDefault(t => t.Id == id);
            // 任务已消失或状态变化则跳过
            if (task == null || task.Status != TaskStatus.Selecting ||
                task.PendingPages is not { Count: > 0 })
                continue;

            _dialogOpen = true;
            try
            {
                var win = new EpisodeSelectWindow(task.Title, task.PendingPages)
                {
                    Owner = System.Windows.Application.Current.MainWindow,
                };
                if (win.ShowDialog() == true)
                    Service.ConfirmEpisodes(task.Id, win.Selected);
                else
                    Service.SkipEpisodes(task.Id);
            }
            finally
            {
                _dialogOpen = false;
                ShowNextPrompt();   // 关窗后立即弹下一个排队任务
            }
            return;
        }
    }

    private async Task CheckLoginOnStartAsync()
    {
        var result = await Service.CheckLoginAsync();
        if (result is LoginResult.LoggedIn li)
            ShowToast($"已登录: {li.Name}");
        else
            ShowToast("未登录，360P 以上画质需要扫码登录");
    }

    public void ShowToast(string msg)
    {
        Dispatcher.BeginInvoke(() => Toast = msg);
    }

    public void Shutdown()
    {
        Service.Dispose();
    }
}
