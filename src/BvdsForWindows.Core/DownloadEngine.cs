using System.IO;
using System.Net.Http;

namespace BvdsForWindows.Core;

/// <summary>
/// 下载执行引擎（对应 Kotlin DownloadEngine）。
/// 流程（mode="3"）：视频流下载(0-50%) → 音频流下载(50-95%) → ffmpeg 合轨(96%) → 复制到目标(98%)。
/// </summary>
public sealed class DownloadEngine
{
    private readonly ApiClient _api;
    private readonly MediaMerger _merger;
    private readonly Action<string, int, string> _onProgress;
    private readonly Action<string, string, bool> _log;

    public DownloadEngine(
        ApiClient api,
        MediaMerger merger,
        Action<string, int, string> onProgress,
        Action<string, string, bool> log)
    {
        _api = api;
        _merger = merger;
        _onProgress = onProgress;
        _log = log;
    }

    /// <summary>执行下载任务（由 TaskScheduler 调用）</summary>
    public async Task<TaskResult> ExecuteAsync(DownloadTask task, CancellationToken ct = default)
    {
        try
        {
            var outFile = string.IsNullOrWhiteSpace(task.DestPath)
                ? Path.Combine(ConfigStore.DefaultDir, $"{SafeFilename(task.Title)}.mp4")
                : task.DestPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outFile) ?? ".");
            // 固定缓存标识：任务 id 去特殊字符（重试路径稳定且无碰撞）
            var unique = System.Text.RegularExpressions.Regex.Replace(task.Id, "[^a-zA-Z0-9]", "_");
            // 仅重试时断点续传；首次执行从头下载，避免误续传残留 .part
            var resume = task.CurrentRetry > 0;

            return task.Mode switch
            {
                "1" => await DownloadVideoOnlyAsync(task, outFile, resume, ct),
                "2" => await DownloadAudioOnlyAsync(task, outFile, resume, ct),
                _ => await DownloadAndMergeAsync(task, outFile, unique, resume, ct),
            };
        }
        catch (OperationCanceledException)
        {
            return new TaskResult(task.Id, false, Error: "取消");
        }
        catch (Exception e)
        {
            _log(task.Id, $"下载异常: {e.Message}", true);
            return new TaskResult(task.Id, false, Error: e.Message);
        }
    }

    // ── 模式 1：仅视频 ─────────────────────────────

    private async Task<TaskResult> DownloadVideoOnlyAsync(DownloadTask task, string outFile, bool resume, CancellationToken ct)
    {
        var ok = await DownloadFileAsync(task.VideoUrl, outFile, task.Id, 0, 100, resume, ct);
        return ok
            ? new TaskResult(task.Id, true, outFile)
            : new TaskResult(task.Id, false, Error: "视频下载失败");
    }

    // ── 模式 2：仅音频 ─────────────────────────────

    private async Task<TaskResult> DownloadAudioOnlyAsync(DownloadTask task, string outFile, bool resume, CancellationToken ct)
    {
        var m4aPath = System.Text.RegularExpressions.Regex.Replace(outFile, @"\.mp3$", ".m4a");
        var ok = await DownloadFileAsync(task.AudioUrl, m4aPath, task.Id, 0, 100, resume, ct);
        return ok
            ? new TaskResult(task.Id, true, m4aPath)
            : new TaskResult(task.Id, false, Error: "音频下载失败");
    }

    // ── 模式 3：音视频合轨 ────────────────────────

    private async Task<TaskResult> DownloadAndMergeAsync(DownloadTask task, string outFile, string unique, bool resume, CancellationToken ct)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "bvds_cache");
        Directory.CreateDirectory(cacheDir);
        var videoPath = Path.Combine(cacheDir, $"video_{unique}.m4v");
        var audioPath = Path.Combine(cacheDir, $"audio_{unique}.m4a");
        var mergedPath = Path.Combine(cacheDir, $"merged_{unique}.mp4");

        // 下载视频 (0-50%)
        if (!await DownloadFileAsync(task.VideoUrl, videoPath, task.Id, 0, 50, resume, ct))
            return new TaskResult(task.Id, false, Error: "视频流下载失败"); // 保留 .part 供断点续传

        // 下载音频 (50-95%)
        if (!await DownloadFileAsync(task.AudioUrl, audioPath, task.Id, 50, 95, resume, ct))
            return new TaskResult(task.Id, false, Error: "音频流下载失败");

        // 合并
        _log(task.Id, "开始合并音视频...", false);
        _onProgress(task.Id, 96, "merging");
        task.Status = TaskStatus.Merging;
        var merged = await _merger.MergeAsync(videoPath, audioPath, mergedPath, ct);
        TryDelete(videoPath); TryDelete(audioPath);

        if (merged == null)
        {
            TryDelete(mergedPath);
            return new TaskResult(task.Id, false, Error: "合并失败");
        }

        // 复制到目标目录
        _log(task.Id, "合并成功，复制到目标目录...", false);
        _onProgress(task.Id, 98, "copying");
        var copied = CopyFile(mergedPath, outFile);
        TryDelete(mergedPath);

        return copied
            ? new TaskResult(task.Id, true, outFile)
            : new TaskResult(task.Id, false, Error: "写入目标目录失败");
    }

    // ── 文件下载（带进度回调 + Range 断点续传） ──────

    private async Task<bool> DownloadFileAsync(
        string url, string destPath, string taskId, int progressStart, int progressEnd, bool resume, CancellationToken ct)
    {
        try
        {
            var filename = url[(url.LastIndexOf('/') + 1)..];
            var partFile = destPath + ".part";
            long existing = 0;
            if (resume && File.Exists(partFile)) existing = new FileInfo(partFile).Length;
            if (!resume && File.Exists(partFile)) File.Delete(partFile); // 首次执行：清掉残留
            _log(taskId, $"下载文件: {filename} (进度 {progressStart}-{progressEnd}%)" +
                (existing > 0 ? $" 断点续传 from={existing}" : ""), false);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", ApiClient.UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            var host = new Uri(url).Host;
            var cookies = _api.CookieJar.CookieHeaderFor(host);
            if (cookies.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            using var http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate |
                                         System.Net.DecompressionMethods.Brotli,
            })
            { Timeout = TimeSpan.FromMinutes(30) };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            // 已下载完整（Range 超出范围）→ 直接收尾
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                _log(taskId, "文件已完整，跳过下载", false);
                return FinishPart(partFile, destPath);
            }
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent && !resp.IsSuccessStatusCode)
            {
                _log(taskId, $"下载 HTTP {(int)resp.StatusCode} (非可重试)", true);
                if ((int)resp.StatusCode is >= 500 and <= 599) throw new HttpRequestException($"服务端错误 {(int)resp.StatusCode}");
                return false;
            }

            var contentLength = resp.Content.Headers.ContentLength ?? 0;
            var resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
            // 服务端忽略 Range 返回 200 时从头写
            var total = (resumed ? existing : 0L) + contentLength;

            await using (var fs = new FileStream(partFile, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[81920];
                long downloaded = resumed ? existing : 0L;
                var lastReport = 0L;
                int len;
                while ((len = await input.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, len), ct);
                    downloaded += len;
                    var now = Environment.TickCount64;
                    if (total > 0 && now - lastReport > 200)
                    {
                        lastReport = now;
                        var pct = progressStart + (int)(downloaded * (progressEnd - progressStart) / total);
                        _onProgress(taskId, pct, "downloading");
                    }
                }
            }

            // 下载完成：.part → 正式文件
            return FinishPart(partFile, destPath);
        }
        catch (HttpRequestException e)
        {
            // 保留 .part 文件供断点续传，抛给上层重试
            _log(taskId, $"下载 IO 异常: {e.Message}", true);
            throw;
        }
    }

    /// <summary>下载完成后将 .part 重命名为正式文件（失败则复制兜底）</summary>
    private bool FinishPart(string partFile, string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
        try
        {
            File.Move(partFile, destPath);
            return true;
        }
        catch
        {
            var ok = CopyFile(partFile, destPath);
            if (ok) TryDelete(partFile);
            return ok;
        }
    }

    // ── 工具 ───────────────────────────────────────

    private bool CopyFile(string src, string dest)
    {
        try
        {
            File.Copy(src, dest, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            _log("copyFile", $"复制失败 src={src} dest={dest} error={e.Message}", true);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string SafeFilename(string name)
    {
        var n = name;
        var m = System.Text.RegularExpressions.Regex.Match(n, @"《([^《》]+)》");
        if (m.Success) n = m.Groups[1].Value;
        n = System.Text.RegularExpressions.Regex.Replace(n, "[<>:\"“”‘’/\\\\|?*]", "");
        if (n.Length > 100) n = n[..100];
        return n.Trim().Length == 0 ? "untitled" : n.Trim();
    }
}
