using System.Text.Json;

namespace BvdsForWindows.Core;

/// <summary>
/// B站二维码登录全流程（对应 Kotlin LoginManager）。
/// 获取二维码 → 轮询状态 → 成功保存 Cookie。
/// </summary>
public sealed class LoginManager
{
    private readonly ApiClient _api;
    private readonly ConfigStore _config;
    private readonly CookieStore _cookieJar;
    private readonly Action<LoginStatus> _onStatus;
    private readonly Action<byte[]> _onQrImage;   // PNG 字节
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    private CancellationTokenSource? _pollCts;

    public LoginManager(
        ApiClient api,
        ConfigStore config,
        CookieStore cookieJar,
        Action<LoginStatus> onStatus,
        Action<byte[]> onQrImage,
        Action<string> log,
        Action<string> logError)
    {
        _api = api;
        _config = config;
        _cookieJar = cookieJar;
        _onStatus = onStatus;
        _onQrImage = onQrImage;
        _log = log;
        _logError = logError;
    }

    /// <summary>开始登录：获取二维码 → 轮询</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            _onStatus(LoginStatus.Generating);
            _log("开始获取二维码...");
            var qr = await GenerateQrAsync();
            if (qr == null) return;
            _log("二维码 URL 获取成功, 生成图片中...");

            var png = QrCodeGenerator.GeneratePng(qr.Url, 240);
            _log($"二维码生成结果: png长度={png?.Length ?? 0}");
            if (png != null && png.Length > 100)
            {
                _onQrImage(png);
                _log("二维码已推送");
                await PollUntilDoneAsync(qr);
            }
            else
            {
                _logError("二维码图片生成失败");
                _onStatus(LoginStatus.QRError);
            }
        });
    }

    /// <summary>取消/停止登录</summary>
    public void Cancel()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        _onStatus(LoginStatus.Cancelled);
    }

    /// <summary>检查当前登录态（应用启动时）</summary>
    public async Task<LoginResult> CheckLoginAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _api.GetAsync("https://api.bilibili.com/x/web-interface/nav", ct);
            if (!resp.IsOk) return new LoginResult.Guest();
            using var json = JsonDocument.Parse(resp.Body);
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var isLogin = data.TryGetProperty("isLogin", out var il) && il.ValueKind == JsonValueKind.True;
                if (isLogin)
                {
                    var name = data.TryGetProperty("uname", out var un) && un.ValueKind == JsonValueKind.String
                        ? un.GetString() ?? "用户" : "用户";
                    return new LoginResult.LoggedIn(name);
                }
            }
            return new LoginResult.Guest();
        }
        catch
        {
            return new LoginResult.Guest();
        }
    }

    /// <summary>退出登录</summary>
    public void Logout()
    {
        _cookieJar.Clear();
        _config.ClearCookies();
    }

    // ── 内部 ───────────────────────────────────────

    private sealed record QrData(string Url, string Key);

    private async Task<QrData?> GenerateQrAsync()
    {
        try
        {
            var resp = await _api.GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
            if (!resp.IsOk)
            {
                _logError($"获取二维码 HTTP {resp.Status}");
                _onStatus(LoginStatus.QRError);
                return null;
            }
            using var json = JsonDocument.Parse(resp.Body);
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                _logError($"获取二维码失败 code={code.GetInt32()}");
                _onStatus(LoginStatus.QRError);
                return null;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                _logError("获取二维码失败: 无 data");
                _onStatus(LoginStatus.QRError);
                return null;
            }
            var url = data.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var key = data.TryGetProperty("qrcode_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                _logError("获取二维码失败: 字段缺失");
                _onStatus(LoginStatus.QRError);
                return null;
            }
            return new QrData(url, key);
        }
        catch (Exception e)
        {
            _logError($"获取二维码异常: {e.Message}");
            _onStatus(LoginStatus.QRError);
            return null;
        }
    }

    private async Task PollUntilDoneAsync(QrData qr)
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var currentQr = qr;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = await PollQrAsync(currentQr.Key, ct);
                if (status == LoginStatus.Cancelled) return;

                switch (status)
                {
                    case LoginStatus.Success:
                    {
                        var cookies = _cookieJar.CookieString();
                        _config.Cookies = cookies;
                        var result = await CheckLoginAsync(ct);
                        _onStatus(LoginStatus.Success);
                        _log(result is LoginResult.LoggedIn li ? $"登录成功: {li.Name}" : "登录成功");
                        return;
                    }
                    case LoginStatus.Expired:
                    {
                        _onStatus(LoginStatus.Expired);
                        _log("二维码已过期，重新生成");
                        await Task.Delay(1000, ct);
                        var newQr = await GenerateQrAsync();
                        if (newQr != null)
                        {
                            currentQr = newQr; // 换新码继续循环
                            _onStatus(LoginStatus.WaitingScan);
                        }
                        else
                        {
                            return; // 重新生成失败
                        }
                        break;
                    }
                    default:
                        _onStatus(status);
                        await Task.Delay(2000, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                _onStatus(LoginStatus.Cancelled);
            else
            {
                _onStatus(LoginStatus.Timeout);
                _log("登录轮询超时");
            }
        }
        finally
        {
            _pollCts = null;
        }
    }

    private async Task<LoginStatus> PollQrAsync(string key, CancellationToken ct)
    {
        try
        {
            var url = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={key}";
            var resp = await _api.GetAsync(url, ct);
            if (!resp.IsOk) return LoginStatus.WaitingScan; // 网络错误不中断，继续等

            using var json = JsonDocument.Parse(resp.Body);
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                return LoginStatus.WaitingScan;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return LoginStatus.WaitingScan;

            var dcode = data.TryGetProperty("code", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : -1;
            return dcode switch
            {
                0 => LoginStatus.Success,
                86038 => LoginStatus.Expired,
                86090 => LoginStatus.Scanned, // 已扫描，等待确认
                _ => LoginStatus.WaitingScan, // 86101 等待扫描
            };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return LoginStatus.WaitingScan;
        }
    }
}
