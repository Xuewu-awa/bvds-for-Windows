using System.Net;
using System.Net.Http;
using System.Text;

namespace BvdsForWindows.Core;

public sealed record ApiResult(int Status, string Body)
{
    public bool IsOk => Status is >= 200 and < 300;
}

/// <summary>
/// HttpClient 封装（对应 OkHttp ApiClient）。
/// 统一 UA / Referer / Cookie / 超时 / 手动跟跳。
/// </summary>
public sealed class ApiClient
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    public CookieStore CookieJar { get; }

    private readonly HttpClient _client;

    public ApiClient(CookieStore cookieJar)
    {
        CookieJar = cookieJar;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // 手动跟跳，记录重定向链
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli,
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        var host = new Uri(url).Host;
        var cookies = CookieJar.CookieHeaderFor(host);
        if (cookies.Length > 0)
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        return req;
    }

    public async Task<ApiResult> GetAsync(string url, CancellationToken ct = default)
    {
        using var req = BuildRequest(url);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        SaveCookies(resp, url);
        return new ApiResult((int)resp.StatusCode, body);
    }

    /// <summary>手动跟跳（对应 getWithRedirectLog）：返回 (最终 HTML, 最终 URL)</summary>
    public async Task<(string Html, string FinalUrl)?> GetWithRedirectLogAsync(
        string url, Action<string>? log = null, CancellationToken ct = default)
    {
        var current = url;
        for (var hop = 0; hop < 10; hop++)
        {
            using var req = BuildRequest(current);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            SaveCookies(resp, current);
            var code = (int)resp.StatusCode;
            var shortUrl = current.Length > 80 ? current[..80] + "..." : current;
            log?.Invoke($"hop{hop} {shortUrl} code={code}");

            if (code is >= 300 and < 400)
            {
                var loc = resp.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(loc))
                {
                    current = loc.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? loc
                        : new Uri(new Uri(current), loc).ToString();
                    continue;
                }
            }
            if (code is >= 200 and < 300)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (body, resp.RequestMessage?.RequestUri?.ToString() ?? current);
            }
            return null;
        }
        return null;
    }

    private void SaveCookies(HttpResponseMessage resp, string url)
    {
        try
        {
            if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies)) return;
            var host = new Uri(url).Host;
            foreach (var sc in setCookies)
            {
                var first = sc.Split(';')[0];
                var eq = first.IndexOf('=');
                if (eq <= 0) continue;
                var name = first[..eq].Trim();
                var value = first[(eq + 1)..].Trim();
                if (name.Length == 0 || value.Length == 0) continue;
                CookieJar.Set(host, name, value);
            }
        }
        catch { /* cookie 解析失败不影响主流程 */ }
    }

    public void Dispose() => _client.Dispose();
}
