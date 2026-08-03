using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BvdsForWindows.Core;

/// <summary>
/// 任务调度器（对应 Kotlin TaskScheduler）。
/// Channel 并发控制：并发数可配置（默认 5），每任务最多 3 次指数退避重试。
/// </summary>
public sealed class TaskScheduler
{
    public const int MaxRetries = 3;

    private readonly Channel<DownloadTask> _channel = Channel.CreateUnbounded<DownloadTask>();
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly Func<DownloadTask, CancellationToken, Task<TaskResult>> _executor;
    private readonly Action<string, string, bool> _log;
    private readonly Func<int> _maxConcurrentProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private int _running;
    private Task? _loop;

    public event Action? TasksChanged;

    public TaskScheduler(
        Func<DownloadTask, CancellationToken, Task<TaskResult>> executor,
        Action<string, string, bool>? log = null,
        Func<int>? maxConcurrentProvider = null)
    {
        _executor = executor;
        _log = log ?? ((_, _, _) => { });
        _maxConcurrentProvider = maxConcurrentProvider ?? (() => 5);
    }

    /// <summary>当前并发上限（每次实时读取，设置变更即时生效）</summary>
    public int MaxConcurrent => _maxConcurrentProvider();

    public ICollection<DownloadTask> All => _tasks.Values;

    /// <summary>提交一批新任务</summary>
    public void Submit(IEnumerable<DownloadTask> tasks)
    {
        foreach (var t in tasks)
        {
            _tasks[t.Id] = t;
            _channel.Writer.TryWrite(t);
        }
        TasksChanged?.Invoke();
    }

    /// <summary>用户确认分P/分集选择后继续</summary>
    public void ConfirmEpisodes(string taskId, IReadOnlyList<EpisodeSelection> selected)
    {
        if (!_tasks.TryRemove(taskId, out var parent)) return;
        foreach (var sel in selected)
        {
            var child = new DownloadTask
            {
                Id = sel.ChildId,
                Title = $"{parent.Title} - {sel.Part}",
                Url = parent.Url,
                Mode = parent.Mode,
                Quality = parent.Quality,
                Bvid = parent.Bvid,
                Cid = sel.Cid,
                EpId = sel.EpId,
            };
            _tasks[child.Id] = child;
            _channel.Writer.TryWrite(child);
        }
        TasksChanged?.Invoke();
    }

    /// <summary>跳过分P/分集选择，取默认那一项</summary>
    public void SkipEpisodes(string taskId, string partTitle, long cid, long epId, string childTaskId)
    {
        if (!_tasks.TryRemove(taskId, out var parent)) return;
        var child = new DownloadTask
        {
            Id = childTaskId,
            Title = $"{parent.Title} - {partTitle}",
            Url = parent.Url,
            Mode = parent.Mode,
            Quality = parent.Quality,
            Bvid = parent.Bvid,
            Cid = cid,
            EpId = epId,
        };
        _tasks[child.Id] = child;
        _channel.Writer.TryWrite(child);
        TasksChanged?.Invoke();
    }

    /// <summary>重试某个失败任务</summary>
    public void Retry(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        task.Status = TaskStatus.Pending;
        task.Progress = 0;
        task.Error = "";
        task.CurrentRetry = 0;
        _channel.Writer.TryWrite(task);
        TasksChanged?.Invoke();
    }

    /// <summary>清理已完成/已失败的任务</summary>
    public void ClearDone()
    {
        foreach (var kv in _tasks)
        {
            if (kv.Value.Status is TaskStatus.Done or TaskStatus.Failed)
                _tasks.TryRemove(kv.Key, out _);
        }
        TasksChanged?.Invoke();
    }

    /// <summary>启动调度循环</summary>
    public void Start()
    {
        _loop = Task.Run(async () =>
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                await WaitForSlotAsync(_cts.Token);
                _ = Task.Run(() => ExecuteWithRetryAsync(task), CancellationToken.None)
                    .ContinueWith(_ => ReleaseSlot());
            }
        });
    }

    public void Stop() => _cts.Cancel();

    /// <summary>等待并发槽位（实时读取并发上限，设置变更即时生效）</summary>
    private async Task WaitForSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_running < _maxConcurrentProvider())
                {
                    _running++;
                    return;
                }
            }
            await Task.Delay(80, ct);
        }
    }

    private void ReleaseSlot()
    {
        lock (_gate) _running--;
    }

    // ── 内部 ───────────────────────────────────────

    private async Task ExecuteWithRetryAsync(DownloadTask task)
    {
        var delayMs = 500L;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (_cts.IsCancellationRequested) return;
            task.CurrentRetry = attempt; // 供 DownloadEngine 判断是否断点续传
            if (attempt > 0)
            {
                _log(task.Id, $"重试下载 第{attempt}次 (等待{delayMs}ms)...", false);
                try { await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _cts.Token); }
                catch (OperationCanceledException) { return; }
                delayMs *= 2;
            }

            TaskResult result;
            try
            {
                result = await _executor(task, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                result = new TaskResult(task.Id, false, Error: "取消");
            }
            catch (Exception e)
            {
                _log(task.Id, $"任务异常: {e.Message}", true);
                result = new TaskResult(task.Id, false, Error: e.Message);
            }

            if (result.Success)
            {
                task.Status = TaskStatus.Done;
                task.Progress = 100;
                task.DestPath = result.Path;
                task.Error = "";
                TasksChanged?.Invoke();
                return;
            }

            // 等待分P选择：保持 Selecting，等 confirmEpisodes/skipEpisodes 创建子任务
            if (result.Error != null && result.Error.StartsWith("等待"))
            {
                task.Status = TaskStatus.Selecting;
                task.Error = "";
                TasksChanged?.Invoke();
                return;
            }

            // 不可重试的错误直接失败
            if (result.Error != null && (result.Error.Contains("非可重试") || result.Error == "取消"))
            {
                task.Status = TaskStatus.Failed;
                task.Error = result.Error;
                TasksChanged?.Invoke();
                return;
            }
        }
        task.Status = TaskStatus.Failed;
        task.Error = "重试耗尽";
        TasksChanged?.Invoke();
    }
}
