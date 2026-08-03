using BvdsForWindows.Core;

namespace BvdsForWindows.ViewModels;

/// <summary>设置页 VM</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly BvdsService _service;

    private string _downloadDir;
    public string DownloadDir
    {
        get => _downloadDir;
        set
        {
            if (Set(ref _downloadDir, value)) MarkDirty();
        }
    }

    private string _theme = "teal";
    public string Theme
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value)) MarkDirty();
        }
    }

    private int _maxConcurrent;
    /// <summary>最大并发下载数（1-64）</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            if (Set(ref _maxConcurrent, Math.Clamp(value, 1, 64))) MarkDirty();
        }
    }

    private string _logText = "";
    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    private bool _isDirty;
    /// <summary>设置是否被修改过但未保存</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    public RelayCommand BrowseDirCmd { get; }
    public RelayCommand SaveSettingsCmd { get; }
    public RelayCommand RefreshLogCmd { get; }
    public RelayCommand ExportLogCmd { get; }
    public RelayCommand ClearLogCmd { get; }
    public RelayCommand OpenDownloadDirCmd { get; }

    public SettingsViewModel(BvdsService service)
    {
        _service = service;
        _downloadDir = _service.Config.DownloadDir;
        _theme = _service.Config.Theme;
        _maxConcurrent = _service.Config.MaxConcurrent;

        BrowseDirCmd = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择下载目录",
                InitialDirectory = System.IO.Directory.Exists(DownloadDir) ? DownloadDir : "",
            };
            if (dlg.ShowDialog() == true)
                DownloadDir = dlg.FolderName;
        });

        SaveSettingsCmd = new RelayCommand(_ => Save());

        RefreshLogCmd = new RelayCommand(_ => LogText = _service.Logger.AllLogs());
        ClearLogCmd = new RelayCommand(_ =>
        {
            _service.Logger.Clear();
            LogText = "";
        });
        ExportLogCmd = new RelayCommand(_ =>
        {
            var path = _service.Logger.ExportTo(DownloadDir);
            LogText = path != null ? $"日志已导出: {path}" : "导出失败";
        });
        OpenDownloadDirCmd = new RelayCommand(_ =>
        {
            if (System.IO.Directory.Exists(DownloadDir))
                System.Diagnostics.Process.Start("explorer.exe", DownloadDir);
        });
    }

    /// <summary>应用并保存当前修改</summary>
    public void Save()
    {
        _service.Config.DownloadDir = DownloadDir;
        _service.Config.Theme = Theme;
        _service.Config.MaxConcurrent = MaxConcurrent;   // 调度器实时读取，即时生效
        App.ApplyTheme(Theme);   // 立即全局生效
        IsDirty = false;
        if (RefreshLogCmd.CanExecute(null)) RefreshLogCmd.Execute(null);
    }

    /// <summary>丢弃修改，恢复到已保存的配置值</summary>
    public void Discard()
    {
        DownloadDir = _service.Config.DownloadDir;
        Theme = _service.Config.Theme;
        MaxConcurrent = _service.Config.MaxConcurrent;
        IsDirty = false;
    }

    private void MarkDirty()
    {
        // 仅当与已保存配置不一致时才标记（避免恢复操作误标脏）
        if (DownloadDir != _service.Config.DownloadDir ||
            Theme != _service.Config.Theme ||
            MaxConcurrent != _service.Config.MaxConcurrent)
            IsDirty = true;
    }
}
