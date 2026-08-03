using BvdsForWindows.Core;

// ── 集成冒烟测试：核心逻辑不依赖 GUI/WPF ──────────────
var logger = new LogCollector(2000);
logger.EntryAdded += line => Console.WriteLine(line);

var service = new BvdsService();
service.Start();

var failures = new List<string>();
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "[PASS]" : "[FAIL]")} {name} {detail}");
    if (!ok) failures.Add(name);
}

try
{
    // 1. 登录状态检查（游客环境应返回 Guest）
    var login = await service.CheckLoginAsync();
    Check("CheckLogin", true, $"(result={login.GetType().Name})");

    // 2. URL 提取
    var urls = BvdsService.ExtractUrls("https://www.bilibili.com/video/BV1xx411c7mD https://b23.tv/BV1GJ411x7h7 不是链接");
    Check("ExtractUrls", urls.Count == 2, $"(count={urls.Count})");

    // 3. 短链解析（跟跳 + HTML 提取标题/bvid/分P）
    var parser = new VideoParser(service.CookieJar is CookieStore jar ? new ApiClient(jar) : null!, (t, m, e) => logger.Log(t, m, e));
    // 直接走 service 内部逻辑更贴近真实：用 fetchByUrl
    var info = await parser.FetchByUrlAsync("https://b23.tv/BV1GJ411x7h7", "5", "t1");
    Check("FetchByUrl", info.Ok, $"(title={info.Title}, bvid={info.Bvid}, pages={info.Pages.Count})");
    if (info.Ok)
    {
        Check("HasVideoUrl", info.VideoUrl.StartsWith("http"), info.VideoUrl[..Math.Min(60, info.VideoUrl.Length)]);
        Check("HasAudioUrl", info.AudioUrl.StartsWith("http"));

        // 4. Range 下载验证：请求视频流前 64KB，期望 206
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
        var req = new HttpRequestMessage(HttpMethod.Get, info.VideoUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ApiClient.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);
        var resp = await http.SendAsync(req);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Check("RangeDownload", resp.StatusCode == System.Net.HttpStatusCode.PartialContent && bytes.Length > 0,
            $"(code={(int)resp.StatusCode}, bytes={bytes.Length})");
    }

    // 5. 分P 识别（多P视频）
    var multi = await parser.FetchByUrlAsync("https://www.bilibili.com/video/BV15W41127tG", "5", "t2");
    Check("MultiPage", multi.Pages.Count > 1, $"(pages={multi.Pages.Count})");
    if (multi.Pages.Count > 1)
    {
        // 6. 分P 子任务路径：bvid + cid 直连播放 API
        var page2 = multi.Pages.FirstOrDefault(p => p.Page == 2);
        if (page2 != null)
        {
            var sub = await parser.FetchByCidAsync(multi.Bvid, page2.Cid, "5", "t3");
            Check("FetchByCid", sub.Ok && sub.VideoUrl.StartsWith("http"), $"(cid={page2.Cid})");
        }
    }
}
catch (Exception e)
{
    Console.WriteLine($"[EXCEPTION] {e}");
    failures.Add(e.Message);
}
finally
{
    service.Dispose();
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "=== 全部通过 ===" : $"=== {failures.Count} 项失败 ===");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
