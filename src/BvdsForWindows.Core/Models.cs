namespace BvdsForWindows.Core;

/// <summary>任务状态（对应 Kotlin TaskStatus）</summary>
public enum TaskStatus
{
    Pending,
    Selecting,
    Downloading,
    Merging,
    Done,
    Failed,
}

/// <summary>下载任务（对应 Kotlin TaskScheduler.Task）</summary>
public sealed class DownloadTask
{
    public required string Id { get; init; }
    public string Title { get; set; } = "";
    public required string Url { get; init; }
    /// <summary>"1"=仅视频 "2"=仅音频 "3"=音视频合轨 "4"=仅封面</summary>
    public required string Mode { get; init; }
    public required string Quality { get; init; }

    public volatile TaskStatus Status = TaskStatus.Pending;
    public volatile int Progress;
    public volatile string DestPath = "";
    public volatile string Error = "";
    public volatile string Bvid = "";
    public long Cid;                           // 64 位平台读写原子，无需 volatile
    public long EpId;                          // 番剧单集 ep_id，非 0 时按番剧通道下载
    public volatile int CurrentRetry;
    public volatile List<VideoParserPage>? PendingPages; // 分P暂存
    public volatile string VideoUrl = "";      // 解析结果
    public volatile string AudioUrl = "";      // 解析结果
    public volatile string CoverUrl = "";      // 解析结果：封面图地址
}

/// <summary>分P/分集（对应 Kotlin VideoParserPage / Page）</summary>
public sealed record VideoParserPage(int Page, string Part, long Cid, long EpId = 0);

/// <summary>用户确认的分P/分集选择（对应 Kotlin EpisodeSelection）</summary>
public sealed record EpisodeSelection(string Part, long Cid, long EpId, string ChildId);

/// <summary>任务执行结果（对应 Kotlin TaskResult）</summary>
public sealed record TaskResult(string TaskId, bool Success, string Path = "", string Error = "", int Progress = 0, string Status = "downloading");

/// <summary>DASH 音视频流地址对</summary>
public sealed record DashUrls(string VideoUrl, string AudioUrl);

/// <summary>视频解析结果（对应 Kotlin VideoInfo）</summary>
public sealed record VideoInfo
{
    public string Title { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string VideoUrl { get; init; } = "";
    public string AudioUrl { get; init; } = "";
    public string Bvid { get; init; } = "";
    /// <summary>封面图地址（已规范化为 https 原图）</summary>
    public string CoverUrl { get; init; } = "";
    public List<VideoParserPage> Pages { get; init; } = new();
    public string? Error { get; init; }
    public bool Ok => Error == null && !string.IsNullOrWhiteSpace(VideoUrl);
}

/// <summary>登录结果（对应 Kotlin LoginResult）</summary>
public abstract record LoginResult
{
    public sealed record Guest : LoginResult;
    public sealed record LoggedIn(string Name) : LoginResult;
}

/// <summary>登录状态（对应 Kotlin LoginStatus）</summary>
public enum LoginStatus
{
    Generating,
    QRError,
    WaitingScan,
    Scanned,
    Expired,
    Success,
    Timeout,
    Cancelled,
}
