using System.Collections.ObjectModel;
using BvdsForWindows.Core;
using TaskStatus = BvdsForWindows.Core.TaskStatus;

namespace BvdsForWindows.ViewModels;

/// <summary>任务列表页 VM（增量更新：集合只增删，卡片内容走 INotifyPropertyChanged）</summary>
public sealed class TasksViewModel : ViewModelBase
{
    private readonly BvdsService _service;
    private readonly Dictionary<string, TaskItemViewModel> _byId = new();

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();

    public RelayCommand ClearDoneCmd { get; }

    public TasksViewModel(BvdsService service)
    {
        _service = service;
        ClearDoneCmd = new RelayCommand(_ => _service.ClearTasks());
        Refresh();
    }

    /// <summary>与调度器快照同步（UI 线程调用）：新增的添加、消失的移除、保留的刷新属性</summary>
    public void Refresh()
    {
        var current = _service.Scheduler.All.ToList();

        // 移除已消失的任务
        var gone = _byId.Keys.Where(id => current.All(t => t.Id != id)).ToList();
        foreach (var id in gone)
        {
            if (_byId.Remove(id, out var item)) Tasks.Remove(item);
        }

        // 新增任务
        foreach (var t in current)
        {
            if (!_byId.ContainsKey(t.Id))
            {
                var item = new TaskItemViewModel(_service, t);
                _byId[t.Id] = item;
                Tasks.Add(item);
            }
        }

        // 保留的任务：刷新显示字段
        foreach (var t in current)
        {
            if (_byId.TryGetValue(t.Id, out var item)) item.UpdateFromTask(t);
        }
    }
}

/// <summary>单个任务项 VM（属性变化走通知，UI 只更新该卡片）</summary>
public sealed class TaskItemViewModel : ViewModelBase
{
    private readonly BvdsService _service;
    private DownloadTask _task;

    private string _title = "";
    private string _statusText = "等待中";
    private string _statusColor = "#6FAFAF";
    private string _statusBg = "#336FAFAF";
    private int _progress;
    private bool _showProgress;
    private string _error = "";
    private bool _hasError;
    private bool _isSelecting;
    private bool _showOpenFolder;

    public string Id => _task.Id;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusColor { get => _statusColor; private set => Set(ref _statusColor, value); }
    /// <summary>状态胶囊底色（kt 版彩色浅底）</summary>
    public string StatusBg { get => _statusBg; private set => Set(ref _statusBg, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ShowProgress { get => _showProgress; private set => Set(ref _showProgress, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public bool IsSelecting { get => _isSelecting; private set => Set(ref _isSelecting, value); }
    public bool ShowOpenFolder { get => _showOpenFolder; private set => Set(ref _showOpenFolder, value); }

    public string QualityName => ConfigStore.QualityNames.TryGetValue(
        ConfigStore.QualityCode(_task.Quality), out var n) ? n : _task.Quality;

    public RelayCommand SelectCmd { get; }
    public RelayCommand RetryCmd { get; }
    public RelayCommand OpenFolderCmd { get; }

    public TaskItemViewModel(BvdsService service, DownloadTask task)
    {
        _service = service;
        _task = task;
        SelectCmd = new RelayCommand(_ => ShowSelection());
        RetryCmd = new RelayCommand(_ => _service.RetryTask(_task.Id));
        OpenFolderCmd = new RelayCommand(_ =>
        {
            var dir = System.IO.Path.GetDirectoryName(_task.DestPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        });
        UpdateFromTask(task);
    }

    /// <summary>弹出分P/分集多选窗口：勾选确认 → 创建子任务；取消/关闭 → 跳过（取默认项）</summary>
    private void ShowSelection()
    {
        var pages = _task.PendingPages;
        if (pages == null || pages.Count == 0) return;

        var win = new Views.EpisodeSelectWindow(_task.Title, pages)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (win.ShowDialog() == true)
            _service.ConfirmEpisodes(_task.Id, win.Selected);
        else
            _service.SkipEpisodes(_task.Id);
    }

    /// <summary>从调度器任务快照刷新显示字段（增量，不重建）</summary>
    public void UpdateFromTask(DownloadTask task)
    {
        _task = task;
        Title = task.Title;
        Progress = task.Progress;
        Error = task.Error;

        var status = task.Status;
        StatusText = status switch
        {
            TaskStatus.Pending => "等待中",
            TaskStatus.Selecting => "选择分P",
            TaskStatus.Downloading => "下载中",
            TaskStatus.Merging => "合轨中",
            TaskStatus.Done => "已完成",
            TaskStatus.Failed => "失败",
            _ => "未知",
        };
        (StatusColor, StatusBg) = status switch
        {
            TaskStatus.Done => ("#16A34A", "#1416A34A"),
            TaskStatus.Failed => ("#DC2626", "#14DC2626"),
            TaskStatus.Selecting => ("#D97706", "#14D97706"),
            _ => ("#6FAFAF", "#336FAFAF"),
        };
        ShowProgress = status is TaskStatus.Downloading or TaskStatus.Merging or TaskStatus.Pending;
        HasError = task.Error.Length > 0;
        IsSelecting = status == TaskStatus.Selecting;
        ShowOpenFolder = status == TaskStatus.Done && task.DestPath.Length > 0;
    }
}
