using System.Text.Json;

namespace BvdsForWindows.Core;

/// <summary>
/// B站视频信息解析器（对应 Kotlin VideoParser）。
/// 三级回退策略：__playinfo__ → __INITIAL_STATE__ → 播放 API → PGC 通道。
/// </summary>
public sealed class VideoParser
{
    private readonly ApiClient _api;
    private readonly Action<string, string, bool> _log;

    public VideoParser(ApiClient api, Action<string, string, bool> log)
    {
        _api = api;
        _log = log;
    }

    // ── 对外入口 ───────────────────────────────────

    /// <summary>通过 URL 解析视频信息（普通视频入口）</summary>
    public async Task<VideoInfo> FetchByUrlAsync(string url, string qualityKey, string taskId, CancellationToken ct = default)
    {
        var qualityCode = ConfigStore.QualityCode(qualityKey);
        _log(taskId, $"fetchVideoInfo 开始 url={url} qualityCode={qualityCode}", false);

        // 1. 两轮跟跳：第一轮积累 cookie，第二轮取 HTML
        _log(taskId, "第1轮: 跟跳积累 cookie...", false);
        var first = await _api.GetWithRedirectLogAsync(url, m => _log(taskId, m, false), ct);
        if (first == null)
        {
            _log(taskId, "第1轮跟跳失败", true);
            return new VideoInfo { Error = "跟跳失败" };
        }

        _log(taskId, "第2轮: 带 cookie 重新跟跳获取 HTML...", false);
        var second = await _api.GetWithRedirectLogAsync(url, m => _log(taskId, m, false), ct);
        if (second == null) return new VideoInfo { Error = "获取HTML失败" };
        var (html, finalUrl) = second.Value;

        _log(taskId, $"最终 URL={finalUrl}", false);
        _log(taskId, $"HTML 长度={html.Length}", false);

        // 2. 标题
        var title = ExtractTitle(html);
        _log(taskId, $"标题={title}", false);

        // 2.5 分P列表 + bvid（独立提取，任何播放路径成功都带上）
        var pages = ExtractPages(html);
        var bvid = ExtractBvid(html, finalUrl);
        if (pages.Count > 1) _log(taskId, $"分P列表: {pages.Count} 页", false);
        if (!string.IsNullOrWhiteSpace(bvid)) _log(taskId, $"bvid={bvid}", false);

        // 3. sourceId
        var sid = ExtractSourceId(finalUrl);
        _log(taskId, $"sourceId={sid}", false);

        // 4. PGC 通道（番剧/影视）
        var isPgc = url.Contains("/ep") || url.Contains("/bangumi");
        _log(taskId, $"isPgc={isPgc}", false);
        if (isPgc)
        {
            var epMatch = RegexCache.Ep().Match(url);
            if (epMatch.Success)
            {
                var epId = epMatch.Groups[1].Value;
                _log(taskId, $"走 PGC 通道 epId={epId}", false);
                // 整季检测：优先页面内 epList（旧版页面），新版页面（无 __INITIAL_STATE__）走 season API
                var epList = ExtractEpList(html);
                if (epList.Count <= 1)
                {
                    epList = await FetchEpListFromApiAsync(epId, taskId, ct);
                    if (epList.Count > 1) _log(taskId, $"season API 整季 epList: {epList.Count} 集", false);
                }
                if (epList.Count > 1)
                {
                    _log(taskId, $"整季 epList: {epList.Count} 集", false);
                    return new VideoInfo { Title = title, SourceId = sid, Pages = epList };
                }
                var dash = await FetchPgcAsync(epId, qualityCode, taskId, ct);
                return new VideoInfo { Title = title, SourceId = sid, VideoUrl = dash.VideoUrl, AudioUrl = dash.AudioUrl };
            }
        }

        // 5. __playinfo__
        _log(taskId, "尝试解析 __playinfo__...", false);
        var pi = ExtractPlayInfo(html, qualityCode, taskId);
        if (pi != null)
        {
            _log(taskId, "__playinfo__ 解析成功", false);
            return new VideoInfo { Title = title, SourceId = sid, VideoUrl = pi.VideoUrl, AudioUrl = pi.AudioUrl, Bvid = bvid, Pages = pages };
        }

        // 6. __INITIAL_STATE__ → 播放 API
        _log(taskId, "__playinfo__ 未找到，从 __INITIAL_STATE__ 提取 bvid/cid 调播放 API...", false);
        var api = await ExtractViaPlayApiAsync(html, qualityCode, taskId, finalUrl, ct);
        if (api != null)
        {
            return api with
            {
                Title = string.IsNullOrWhiteSpace(api.Title) ? title : api.Title,
                SourceId = sid,
                Bvid = bvid,
                Pages = pages,
            };
        }

        // 7. 尝试 __INITIAL_STATE__ 直接提取
        var init = ExtractFromInitialState(html, qualityCode, taskId);
        if (init != null)
        {
            return new VideoInfo { Title = title, SourceId = sid, VideoUrl = init.VideoUrl, AudioUrl = init.AudioUrl, Bvid = bvid, Pages = pages };
        }

        _log(taskId, "所有解析方式均失败", true);
        return new VideoInfo { Error = "无法解析视频信息" };
    }

    /// <summary>通过 bvid + cid 直接获取（合集分P下载使用）</summary>
    public async Task<VideoInfo> FetchByCidAsync(string bvid, long cid, string qualityKey, string taskId, CancellationToken ct = default)
    {
        var qualityCode = ConfigStore.QualityCode(qualityKey);
        _log(taskId, $"fetchVideoInfoByCid bvid={bvid} cid={cid} qualityCode={qualityCode}", false);
        var dash = await FetchPlayApiDashAsync(bvid, cid, qualityCode, taskId, ct);
        return dash != null
            ? new VideoInfo { VideoUrl = dash.VideoUrl, AudioUrl = dash.AudioUrl, Bvid = bvid }
            : new VideoInfo { Error = "播放 API 获取失败" };
    }

    /// <summary>通过 ep_id 直接获取番剧单集（整季子任务下载使用）</summary>
    public async Task<VideoInfo> FetchByEpIdAsync(long epId, string qualityKey, string taskId, CancellationToken ct = default)
    {
        var qualityCode = ConfigStore.QualityCode(qualityKey);
        _log(taskId, $"fetchByEpId epId={epId} qualityCode={qualityCode}", false);
        var dash = await FetchPgcAsync(epId.ToString(), qualityCode, taskId, ct);
        return new VideoInfo { VideoUrl = dash.VideoUrl, AudioUrl = dash.AudioUrl };
    }

    // ── HTML 提取 ──────────────────────────────────

    /// <summary>通过 pgc/view/web/season API 获取整季剧集列表（新版页面无 __INITIAL_STATE__ 时使用）</summary>
    private async Task<List<VideoParserPage>> FetchEpListFromApiAsync(
        string epId, string taskId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.bilibili.com/pgc/view/web/season?ep_id={epId}";
            _log(taskId, $"season API 请求: {url}", false);
            var resp = await _api.GetAsync(url, ct);
            using var json = JsonDocument.Parse(resp.Body);
            if (!json.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
                return new List<VideoParserPage>();
            if (!json.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("episodes", out var eps))
                return new List<VideoParserPage>();

            var list = new List<VideoParserPage>();
            var idx = 1;
            foreach (var ep in eps.EnumerateArray())
            {
                var id = GetLong(ep, "id");
                if (id <= 0) continue;
                var title = GetString(ep, "long_title");
                if (string.IsNullOrWhiteSpace(title)) title = GetString(ep, "title");
                list.Add(new VideoParserPage(idx++, title, 0, id));
            }
            return list;
        }
        catch (Exception e)
        {
            _log(taskId, $"season API 异常: {e.Message}", true);
            return new List<VideoParserPage>();
        }
    }

    /// <summary>从 __INITIAL_STATE__.epList 提取番剧整季集数列表</summary>
    private List<VideoParserPage> ExtractEpList(string html)
    {
        var stateJson = ExtractJsonAfterAssignment(html, "__INITIAL_STATE__");
        if (stateJson == null) return new List<VideoParserPage>();
        try
        {
            using var state = JsonDocument.Parse(stateJson);
            if (!state.RootElement.TryGetProperty("epList", out var epList)) return new();
            var result = new List<VideoParserPage>();
            foreach (var ep in epList.EnumerateArray())
            {
                var id = GetLong(ep, "id");
                if (id <= 0) continue;
                var title = GetString(ep, "long_title");
                if (string.IsNullOrWhiteSpace(title)) title = GetString(ep, "title");
                result.Add(new VideoParserPage(GetInt(ep, "index"), title, 0, id));
            }
            return result;
        }
        catch { return new List<VideoParserPage>(); }
    }

    /// <summary>从 __INITIAL_STATE__.videoData.pages 提取分P列表</summary>
    private List<VideoParserPage> ExtractPages(string html)
    {
        var stateJson = ExtractJsonAfterAssignment(html, "__INITIAL_STATE__");
        if (stateJson == null) return new List<VideoParserPage>();
        try
        {
            using var state = JsonDocument.Parse(stateJson);
            if (!state.RootElement.TryGetProperty("videoData", out var vd)) return new();
            if (!vd.TryGetProperty("pages", out var pagesJson)) return new();
            var result = new List<VideoParserPage>();
            foreach (var pg in pagesJson.EnumerateArray())
            {
                var cid = GetLong(pg, "cid");
                if (cid <= 0) continue;
                result.Add(new VideoParserPage(GetInt(pg, "page"), GetString(pg, "part"), cid));
            }
            return result;
        }
        catch { return new List<VideoParserPage>(); }
    }

    /// <summary>提取 bvid：优先 __INITIAL_STATE__，后备 finalUrl 的 /video/BVxxx 路径</summary>
    private string ExtractBvid(string html, string finalUrl)
    {
        var stateJson = ExtractJsonAfterAssignment(html, "__INITIAL_STATE__");
        if (stateJson != null)
        {
            try
            {
                using var state = JsonDocument.Parse(stateJson);
                var bvid = "";
                if (state.RootElement.TryGetProperty("videoData", out var vd))
                    bvid = GetString(vd, "bvid");
                if (string.IsNullOrEmpty(bvid))
                    bvid = GetString(state.RootElement, "bvid");
                if (!string.IsNullOrEmpty(bvid)) return bvid;
            }
            catch { }
        }
        var m = RegexCache.VideoBv().Match(finalUrl);
        return m.Success ? m.Value[(m.Value.IndexOf("BV", StringComparison.Ordinal) + 2)..] : "";
    }

    private string ExtractTitle(string html)
    {
        var m = RegexCache.Title().Match(html);
        if (!m.Success) return "未知标题";
        var t = m.Groups[1].Value.Replace("_哔哩哔哩_bilibili", "").Trim();
        return string.IsNullOrWhiteSpace(t) ? "未知标题" : t;
    }

    private string ExtractSourceId(string url)
    {
        if (RegexCache.Bv().Match(url) is { Success: true } bv) return $"BV:{bv.Value}";
        if (RegexCache.Ep().Match(url) is { Success: true } ep) return $"EP:{ep.Groups[1].Value}";
        if (RegexCache.Ss().Match(url) is { Success: true } ss) return $"SS:{ss.Groups[1].Value}";
        return $"URL:{url[..Math.Min(30, url.Length)]}";
    }

    // ── __playinfo__ 解析 ─────────────────────────

    private DashUrls? ExtractPlayInfo(string html, int qualityCode, string taskId)
    {
        var rawJson = ExtractJsonAfterAssignment(html, "window.__playinfo__")
            ?? ExtractJsonAfterAssignment(html, "__playinfo__")
            ?? ExtractJsonAfterAssignment(html, "\"__playinfo__\"")
            ?? ExtractJsonAfterAssignment(html, "'__playinfo__'");
        if (rawJson == null) return null;
        if (rawJson.Length < 200)
        {
            _log(taskId, $"提取的 JSON 太短 ({rawJson.Length}字节)，可能是误提取，跳过", true);
            return null;
        }
        _log(taskId, $"提取到 JSON 长度={rawJson.Length}, 前80字={rawJson[..Math.Min(80, rawJson.Length)]}", false);

        try
        {
            using var playData = JsonDocument.Parse(rawJson);
            if (!playData.RootElement.TryGetProperty("data", out var data))
            {
                _log(taskId, "无 data 字段", true);
                return null;
            }
            if (!data.TryGetProperty("dash", out var dash))
            {
                _log(taskId, "无 dash 字段", true);
                return null;
            }
            return ExtractDashUrls(dash, qualityCode, taskId);
        }
        catch (Exception e)
        {
            _log(taskId, $"JSON 解析异常: {e.Message}", true);
            return null;
        }
    }

    // ── __INITIAL_STATE__ 解析 ────────────────────

    private DashUrls? ExtractFromInitialState(string html, int qualityCode, string taskId)
    {
        var rawJson = ExtractJsonAfterAssignment(html, "__INITIAL_STATE__");
        if (rawJson == null) return null;
        _log(taskId, $"__INITIAL_STATE__ JSON 长度={rawJson.Length}", false);
        try
        {
            using var state = JsonDocument.Parse(rawJson);
            var keys = string.Join(", ", state.RootElement.EnumerateObject().Select(p => p.Name));
            _log(taskId, $"__INITIAL_STATE__ 顶层 keys: {keys}", false);

            // 穷举路径
            foreach (var key in new[] { "videoData", "videoInfo", "playInfo", "playinfo", "initPlayInfo" })
            {
                if (state.RootElement.TryGetProperty(key, out var pd) && pd.ValueKind == JsonValueKind.Object &&
                    (pd.TryGetProperty("dash", out _) || pd.TryGetProperty("data", out _)))
                {
                    _log(taskId, $"在 {key} 中找到播放数据", false);
                    return ExtractDashFromPlayData(pd, qualityCode, taskId);
                }
            }

            // videoData 内部
            if (state.RootElement.TryGetProperty("videoData", out var vd) && vd.ValueKind == JsonValueKind.Object)
            {
                var vdKeys = string.Join(", ", vd.EnumerateObject().Select(p => p.Name));
                _log(taskId, $"videoData 子 keys: {vdKeys}", false);
                if (vd.TryGetProperty("dash", out _))
                {
                    _log(taskId, "在 videoData 自身找到 dash", false);
                    return ExtractDashFromPlayData(vd, qualityCode, taskId);
                }
                if (vd.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object &&
                    inner.TryGetProperty("dash", out _))
                {
                    _log(taskId, "在 videoData.data 中找到 dash", false);
                    return ExtractDashFromPlayData(inner, qualityCode, taskId);
                }
            }

            // player
            if (state.RootElement.TryGetProperty("player", out var player) && player.ValueKind == JsonValueKind.Object)
            {
                var pKeys = string.Join(", ", player.EnumerateObject().Select(p => p.Name));
                _log(taskId, $"player 子 keys: {pKeys}", false);
                if (player.TryGetProperty("dash", out _))
                {
                    _log(taskId, "在 player 中找到 dash", false);
                    return ExtractDashFromPlayData(player, qualityCode, taskId);
                }
            }

            _log(taskId, "__INITIAL_STATE__ 中未找到播放数据", true);
            return null;
        }
        catch (Exception e)
        {
            _log(taskId, $"__INITIAL_STATE__ 解析异常: {e.Message}", true);
            return null;
        }
    }

    // ── 播放 API ──────────────────────────────────

    private async Task<VideoInfo?> ExtractViaPlayApiAsync(
        string html, int qualityCode, string taskId, string finalUrl, CancellationToken ct)
    {
        var stateJson = ExtractJsonAfterAssignment(html, "__INITIAL_STATE__");
        if (stateJson == null)
        {
            _log(taskId, "未找到 __INITIAL_STATE__", true);
            return null;
        }
        try
        {
            using var state = JsonDocument.Parse(stateJson);
            if (!state.RootElement.TryGetProperty("videoData", out var vd))
            {
                _log(taskId, "无 videoData", true);
                return null;
            }
            var bvid = GetString(vd, "bvid");
            if (string.IsNullOrEmpty(bvid)) bvid = GetString(state.RootElement, "bvid");
            var cid = GetLong(vd, "cid");
            var hasPages = vd.TryGetProperty("pages", out var pagesJson);

            // 分P
            var page = 1;
            var pageMatch = RegexCache.ParamP().Match(finalUrl);
            if (pageMatch.Success) int.TryParse(pageMatch.Groups[1].Value, out page);
            if (page > 1 && hasPages && pagesJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var pg in pagesJson.EnumerateArray())
                {
                    if (GetInt(pg, "page") == page)
                    {
                        var pcid = GetLong(pg, "cid");
                        if (pcid > 0) cid = pcid;
                        break;
                    }
                }
            }

            _log(taskId, $"bvid={bvid} cid={cid}", false);
            if (string.IsNullOrEmpty(bvid) || cid == 0)
            {
                _log(taskId, "缺少 bvid 或 cid", true);
                return null;
            }

            var dash = await FetchPlayApiDashAsync(bvid, cid, qualityCode, taskId, ct);
            if (dash == null) return null;

            var pages = new List<VideoParserPage>();
            if (hasPages && pagesJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var pg in pagesJson.EnumerateArray())
                {
                    var pcid = GetLong(pg, "cid");
                    if (pcid <= 0) continue;
                    pages.Add(new VideoParserPage(GetInt(pg, "page"), GetString(pg, "part"), pcid));
                }
            }
            return new VideoInfo { VideoUrl = dash.VideoUrl, AudioUrl = dash.AudioUrl, Bvid = bvid, Pages = pages };
        }
        catch (Exception e)
        {
            _log(taskId, $"播放 API 异常: {e.Message}", true);
            return null;
        }
    }

    // ── PGC ───────────────────────────────────────

    private async Task<DashUrls> FetchPgcAsync(
        string epId, int qualityCode, string taskId, CancellationToken ct)
    {
        var url = $"https://api.bilibili.com/pgc/player/web/playurl?ep_id={epId}&qn={qualityCode}&fnval=4048&fourk=1";
        _log(taskId, $"PGC API 请求: {url}", false);
        var resp = await _api.GetAsync(url, ct);
        _log(taskId, $"PGC 响应长度={resp.Body.Length}", false);

        using var json = JsonDocument.Parse(resp.Body);
        if (json.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
            throw new Exception("PGC API 返回错误");
        if (!json.RootElement.TryGetProperty("result", out var result))
            throw new Exception("无 PGC 数据");
        if (!result.TryGetProperty("dash", out var dash))
            throw new Exception("无 PGC dash 数据");

        return ExtractDashUrls(dash, qualityCode, taskId)
            ?? throw new Exception("PGC dash 为空");
    }

    // ── 播放 API 通用 ─────────────────────────────

    private async Task<DashUrls?> FetchPlayApiDashAsync(
        string bvid, long cid, int qualityCode, string taskId, CancellationToken ct)
    {
        var url = $"https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&qn={qualityCode}&fnval=4048&fourk=1";
        _log(taskId, $"调播放 API: {url[..Math.Min(100, url.Length)]}...", false);
        var resp = await _api.GetAsync(url, ct);
        _log(taskId, $"播放 API 响应长度={resp.Body.Length}", false);

        using var json = JsonDocument.Parse(resp.Body);
        if (json.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
        {
            var msg = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            _log(taskId, $"播放 API 错误: {msg}", true);
            return null;
        }
        JsonElement result;
        if (json.RootElement.TryGetProperty("data", out result) || json.RootElement.TryGetProperty("result", out result))
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                _log(taskId, "播放 API 无 data/result", true);
                return null;
            }
        }
        else
        {
            var keys = string.Join(", ", json.RootElement.EnumerateObject().Select(p => p.Name));
            _log(taskId, $"播放 API 顶层 keys: {keys} — 无 data/result", true);
            return null;
        }
        if (!result.TryGetProperty("dash", out var dash))
        {
            _log(taskId, "播放 API 无 dash", true);
            return null;
        }
        return ExtractDashUrls(dash, qualityCode, taskId);
    }

    // ── DASH 提取通用 ─────────────────────────────

    private DashUrls? ExtractDashUrls(JsonElement dash, int qualityCode, string taskId)
    {
        var hasVideos = dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array;
        var hasAudios = dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array;
        _log(taskId, $"video={(hasVideos ? videos.GetArrayLength() : 0)} audio={(hasAudios ? audios.GetArrayLength() : 0)}", false);
        if (!hasVideos || videos.GetArrayLength() == 0 || !hasAudios || audios.GetArrayLength() == 0)
        {
            _log(taskId, "video/audio 列表为空", true);
            return null;
        }

        // 选视频：精确匹配 qualityCode，或最接近；同清晰度下优先 AVC > HEVC > AV1
        var bestV = videos[0];
        var bestDiff = Math.Abs(GetInt(bestV, "id") - qualityCode);
        var bestRank = CodecRank(GetString(bestV, "codecs"));
        foreach (var obj in videos.EnumerateArray())
        {
            var vid = GetInt(obj, "id");
            var rank = CodecRank(GetString(obj, "codecs"));
            var diff = Math.Abs(vid - qualityCode);
            if (diff < bestDiff || (diff == bestDiff && rank < bestRank))
            {
                bestDiff = diff; bestRank = rank; bestV = obj;
            }
        }
        _log(taskId, $"选中视频 id={GetInt(bestV, "id")} codecs={GetString(bestV, "codecs")}", false);

        // 选最高码率音频
        var bestA = audios[0];
        var bestBw = GetLong(bestA, "bandwidth");
        foreach (var obj in audios.EnumerateArray())
        {
            var bw = GetLong(obj, "bandwidth");
            if (bw > bestBw) { bestBw = bw; bestA = obj; }
        }
        _log(taskId, $"选中音频 bandwidth={bestBw}", false);

        var videoUrl = GetString(bestV, "baseUrl");
        if (string.IsNullOrEmpty(videoUrl)) videoUrl = GetString(bestV, "base_url");
        var audioUrl = GetString(bestA, "baseUrl");
        if (string.IsNullOrEmpty(audioUrl)) audioUrl = GetString(bestA, "base_url");

        return string.IsNullOrEmpty(videoUrl) || string.IsNullOrEmpty(audioUrl)
            ? null
            : new DashUrls(videoUrl, audioUrl);
    }

    /// <summary>codec 优先级：AVC=0 > HEVC=1 > AV1=2 > 其他=3（值越小越优先）</summary>
    private static int CodecRank(string? codecs) => codecs switch
    {
        null => 3,
        _ when codecs.StartsWith("avc", StringComparison.OrdinalIgnoreCase) => 0,
        _ when codecs.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
               codecs.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) => 1,
        _ when codecs.StartsWith("av01", StringComparison.OrdinalIgnoreCase) => 2,
        _ => 3,
    };

    private DashUrls? ExtractDashFromPlayData(JsonElement pd, int qualityCode, string taskId)
    {
        var data = pd.ValueKind == JsonValueKind.Object && pd.TryGetProperty("data", out var d) ? d : pd;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("dash", out var dash))
        {
            _log(taskId, "无 dash 字段", true);
            return null;
        }
        return ExtractDashUrls(dash, qualityCode, taskId);
    }

    // ── JSON 工具 ─────────────────────────────────

    private static long GetLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt64() : 0;

    private static int GetInt(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : 0;

    private static string GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? "" : "";

    /// <summary>在 html 中查找 key 后紧跟 = 或 : 再跟 { 的 JSON 赋值，数括号提取。</summary>
    public static string? ExtractJsonAfterAssignment(string html, string key)
    {
        var searchFrom = 0;
        while (searchFrom < html.Length)
        {
            var idx = html.IndexOf(key, searchFrom, StringComparison.Ordinal);
            if (idx < 0) return null;

            var after = idx + key.Length;
            while (after < html.Length && char.IsWhiteSpace(html[after])) after++;
            if (after >= html.Length) { searchFrom = idx + 1; continue; }
            var sep = html[after];
            if (sep != '=' && sep != ':') { searchFrom = idx + 1; continue; }

            var brace = html.IndexOf('{', after + 1);
            if (brace < 0) { searchFrom = idx + 1; continue; }

            // 确保 { 和赋值符之间只有空白
            var between = html.Substring(after + 1, brace - after - 1);
            if (between.Any(c => !char.IsWhiteSpace(c))) { searchFrom = idx + 1; continue; }

            // 数括号
            var depth = 0;
            var end = -1;
            for (var i = brace; i < html.Length; i++)
            {
                if (html[i] == '{') depth++;
                else if (html[i] == '}') { depth--; if (depth == 0) { end = i + 1; break; } }
            }
            if (end < 0) { searchFrom = idx + 1; continue; }

            var json = html.Substring(brace, end - brace).Trim();
            if (json.EndsWith(';')) json = json[..^1].Trim();
            return json;
        }
        return null;
    }
}

internal static partial class RegexCache
{
    [System.Text.RegularExpressions.GeneratedRegex(@"ep(\d+)")]
    public static partial System.Text.RegularExpressions.Regex Ep();

    [System.Text.RegularExpressions.GeneratedRegex(@"/video/BV[0-9a-zA-Z]{10}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    public static partial System.Text.RegularExpressions.Regex VideoBv();

    [System.Text.RegularExpressions.GeneratedRegex(@"<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    public static partial System.Text.RegularExpressions.Regex Title();

    [System.Text.RegularExpressions.GeneratedRegex(@"BV[0-9a-zA-Z]{10}")]
    public static partial System.Text.RegularExpressions.Regex Bv();

    [System.Text.RegularExpressions.GeneratedRegex(@"ss(\d+)")]
    public static partial System.Text.RegularExpressions.Regex Ss();

    [System.Text.RegularExpressions.GeneratedRegex(@"[?&]p=(\d+)")]
    public static partial System.Text.RegularExpressions.Regex ParamP();
}
