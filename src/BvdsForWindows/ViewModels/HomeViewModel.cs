using System.Collections.ObjectModel;
using BvdsForWindows.Core;

namespace BvdsForWindows.ViewModels;

/// <summary>主页（下载）VM</summary>
public sealed class HomeViewModel : ViewModelBase
{
    private readonly BvdsService _service;

    private string _urlText = "";
    public string UrlText
    {
        get => _urlText;
        set => Set(ref _urlText, value);
    }

    /// <summary>下载模式："1"=仅视频 "2"=仅音频 "3"=合轨</summary>
    private string _mode = "3";
    public string Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    private string _quality = "2";
    public string Quality
    {
        get => _quality;
        set => Set(ref _quality, value);
    }

    public ObservableCollection<QualityOption> QualityOptions { get; } = new()
    {
        new("1", "4K 超高清"),
        new("2", "1080P 高清"),
        new("3", "720P 准高清"),
        new("4", "480P 清晰"),
        new("5", "360P 流畅"),
    };

    private string _status = "就绪";
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public AsyncRelayCommand StartCmd { get; }

    public HomeViewModel(BvdsService service)
    {
        _service = service;
        _quality = _service.Config.Quality;
        StartCmd = new AsyncRelayCommand(_ => StartAsync());
    }

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(UrlText))
        {
            Status = "请先粘贴 B站链接";
            return;
        }

        // C 盘下载目录提醒（可勾选"以后不再提醒"）
        if (!await ConfirmDownloadDirAsync())
        {
            Status = "已取消下载";
            return;
        }

        Status = "正在添加任务...";
        var (ok, msg) = await _service.StartDownloadAsync(UrlText, Mode, Quality);
        Status = msg;
        if (ok) UrlText = "";
    }

    /// <summary>检查下载目录是否在 C 盘，是则弹窗提醒。返回 false = 用户取消下载。</summary>
    private Task<bool> ConfirmDownloadDirAsync()
    {
        var dir = _service.Config.DownloadDir;
        var root = System.IO.Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root) ||
            !root.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
            _service.Config["no_c_drive_warning"] == "1")
        {
            return Task.FromResult(true);   // 不在 C 盘或已勾选不再提醒
        }

        var win = new Views.CdriveWarningWindow(dir)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (win.ShowDialog() != true) return Task.FromResult(false);   // 取消下载

        if (win.DontAskAgain)
            _service.Config["no_c_drive_warning"] = "1";   // 持久化"以后不提醒"

        if (win.ChangedDir)
        {
            _service.Config.DownloadDir = win.NewDir;
            Status = $"下载目录已改为: {win.NewDir}";
        }
        return Task.FromResult(true);
    }

    /// <summary>任务开始后由主 VM 调用刷新状态</summary>
    public void Refresh() { }
}

public sealed record QualityOption(string Key, string Name)
{
    public override string ToString() => Name;
}
