using System.IO;

namespace BvdsForWindows.Core;

/// <summary>
/// 应用服务组合根（对应 Android BvdsEngine）：组装所有模块，向 UI 层暴露命令。
/// </summary>
public sealed class BvdsService : IDisposable
{
    public ConfigStore Config { get; }
    public CookieStore CookieJar { get; }
    public LogCollector Logger { get; }
    public TaskScheduler Scheduler { get; }
    public LoginManager Login { get; private set; }

    private readonly ApiClient _api;
    private readonly VideoParser _parser;
    private readonly DownloadEngine _engine;
    private readonly MediaMerger _merger;

    /// <summary>任务列表变化（UI 刷新）</summary>
    public event Action? TasksChanged;

    private long _lastProgressNotify;
    private const long ProgressNotifyIntervalMs = 400;

    public BvdsService()
    {
        Config = new ConfigStore();
        CookieJar = new CookieStore(Config);
        Logger = new LogCollector();
        _api = new ApiClient(CookieJar);
        _merger = new MediaMerger(msg => Logger.Log("merge", msg));

        _parser = new VideoParser(_api, (tag, msg, isErr) => Logger.Log(tag, msg, isErr));

        _engine = new DownloadEngine(
            _api, _merger,
            onProgress: (id, pct, status) =>
            {
                var t = Scheduler.All.FirstOrDefault(t => t.Id == id);
                if (t == null) return;
                t.Progress = pct;
                // 节流触发 UI 刷新（进度回调 ~200ms 一次）
                var now = Environment.TickCount64;
                if (now - _lastProgressNotify > ProgressNotifyIntervalMs)
                {
                    _lastProgressNotify = now;
                    TasksChanged?.Invoke();
                }
            },
            log: (tag, msg, isErr) => Logger.Log(tag, msg, isErr));

        Scheduler = new TaskScheduler(
            executor: HandleTaskExecutionAsync,
            log: (tag, msg, isErr) => Logger.Log(tag, msg, isErr),
            maxConcurrentProvider: () => Config.MaxConcurrent);
        Scheduler.TasksChanged += () => TasksChanged?.Invoke();

        Login = CreateLoginManager();
        if (Config.Cookies.Length > 0)
            Logger.Log("cookies", "已加载保存的 Cookie");
    }

    private LoginManager CreateLoginManager() => new(
        _api, Config, CookieJar,
        onStatus: _ => { },
        onQrImage: _ => { },
        log: msg => Logger.Log("login", msg),
        logError: msg => Logger.Log("login", msg, true));

    /// <summary>启动调度器（应用启动时调用）</summary>
    public void Start()
    {
        Scheduler.Start();
    }

    /// <summary>开始登录（UI 传入状态/二维码回调）</summary>
    public void StartLogin(Action<LoginStatus> onStatus, Action<byte[]> onQrImage)
    {
        Login = new LoginManager(
            _api, Config, CookieJar,
            onStatus, onQrImage,
            msg => Logger.Log("login", msg),
            msg => Logger.Log("login", msg, true));
        Login.Start();
    }

    public void CancelLogin() => Login.Cancel();
    public void Logout() => Login.Logout();

    public Task<LoginResult> CheckLoginAsync(CancellationToken ct = default) => Login.CheckLoginAsync(ct);

    /// <summary>添加下载任务（对应 startDownload）</summary>
    public async Task<(bool Ok, string Message)> StartDownloadAsync(string text, string mode, string quality)
    {
        Config.Quality = quality;

        // 360P 以上需要登录
        if (quality != "5")
        {
            var loginResult = await Login.CheckLoginAsync();
            if (loginResult is LoginResult.Guest)
                return (false, "360P 以上画质需要登录，请先扫码登录");
        }

        var urls = ExtractUrls(text);
        if (urls.Count == 0) return (false, "未识别到有效B站链接");

        var tasks = urls.Select(url => new DownloadTask
        {
            Id = $"task_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}",
            Title = "识别中...",
            Url = url,
            Mode = mode,
            Quality = quality,
        }).ToList();

        Scheduler.Submit(tasks);
        return (true, $"已添加 {urls.Count} 个下载任务");
    }

    /// <summary>确认分P/分集选择</summary>
    public void ConfirmEpisodes(string taskId, IReadOnlyList<EpisodeSelection> selected)
    {
        if (selected.Count > 0) Scheduler.ConfirmEpisodes(taskId, selected);
    }

    /// <summary>跳过分P/分集选择：分P取 URL p 参数指向的那一P；番剧取第一集（默认）</summary>
    public void SkipEpisodes(string taskId)
    {
        var task = Scheduler.All.FirstOrDefault(t => t.Id == taskId);
        if (task == null) return;
        var pages = task.PendingPages ?? new List<VideoParserPage>();
        if (pages.Count == 0) return;

        var pageNum = 1;
        var m = System.Text.RegularExpressions.Regex.Match(task.Url, @"[?&]p=(\d+)");
        if (m.Success) int.TryParse(m.Groups[1].Value, out pageNum);
        var target = pages.FirstOrDefault(p => p.Page == pageNum) ?? pages[0];
        Logger.Log(taskId, $"skipEpisodes 跳过选择，取第 {target.Page} 项 (cid={target.Cid} epId={target.EpId})");
        Scheduler.SkipEpisodes(taskId, target.Part, target.Cid, target.EpId,
            $"task_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}");
    }

    public void RetryTask(string taskId) => Scheduler.Retry(taskId);
    public void ClearTasks() => Scheduler.ClearDone();

    // ── 内部：任务执行协调器（对应 handleTaskExecution） ──

    private async Task<TaskResult> HandleTaskExecutionAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            // 1. 视频解析（番剧按 epId 走 PGC 通道；分P按 bvid+cid；普通走 URL）
            Logger.Log(task.Id, $"开始解析 {task.Url}");
            VideoInfo info;
            if (task.EpId > 0)
                info = await _parser.FetchByEpIdAsync(task.EpId, task.Quality, task.Id, ct);
            else if (task.Cid > 0 && !string.IsNullOrWhiteSpace(task.Bvid))
                info = await _parser.FetchByCidAsync(task.Bvid, task.Cid, task.Quality, task.Id, ct);
            else
                info = await _parser.FetchByUrlAsync(task.Url, task.Quality, task.Id, ct);

            if (info.Error != null)
                return new TaskResult(task.Id, false, Error: info.Error);

            // 更新标题
            if (!string.IsNullOrWhiteSpace(info.Title)) task.Title = info.Title;
            task.Bvid = info.Bvid;
            TasksChanged?.Invoke();

            // 分P/分集检测 → 等待用户选择（子任务已有 cid/epId 则不弹窗；番剧无 bvid 不要求）
            var hasEpisodes = info.Pages.Any(p => p.EpId > 0);
            if (task.Cid == 0 && task.EpId == 0 && info.Pages.Count > 1 &&
                (hasEpisodes || !string.IsNullOrWhiteSpace(info.Bvid)))
            {
                task.PendingPages = info.Pages;
                task.Status = TaskStatus.Selecting;
                TasksChanged?.Invoke();
                return new TaskResult(task.Id, false, Error: "等待分P选择");
            }

            if (string.IsNullOrWhiteSpace(info.VideoUrl))
                return new TaskResult(task.Id, false, Error: "无法获取视频链接");

            // 2. 设置路径和 URL
            var dir = Config.DownloadDir;
            var ext = task.Mode == "2" ? ".mp3" : ".mp4";
            task.DestPath = Path.Combine(dir, DownloadEngine.SafeFilename(task.Title) + ext);
            task.Status = TaskStatus.Downloading;
            task.VideoUrl = info.VideoUrl;
            task.AudioUrl = info.AudioUrl;

            // 3. 执行下载
            return await _engine.ExecuteAsync(task, ct);
        }
        catch (OperationCanceledException)
        {
            return new TaskResult(task.Id, false, Error: "取消");
        }
        catch (Exception e)
        {
            Logger.Log(task.Id, $"任务异常: {e.Message}", true);
            return new TaskResult(task.Id, false, Error: e.Message);
        }
    }

    /// <summary>从文本中提取 B站链接（支持空格/逗号/分号/换行分隔）</summary>
    public static List<string> ExtractUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.StartsWith("http://") || t.StartsWith("https://") ||
                        t.Contains("bilibili.com") || t.Contains("b23.tv"))
            .Select(t => t.StartsWith("http") ? t : "https://" + t)
            .Distinct()
            .ToList();
    }

    public void Dispose()
    {
        Scheduler.Stop();
        _api.Dispose();
    }
}
