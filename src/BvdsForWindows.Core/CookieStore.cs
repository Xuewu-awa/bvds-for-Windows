namespace BvdsForWindows.Core;

/// <summary>
/// Cookie 池（对应 PersistentCookieJar）。
/// 内存中按 domain 维护，持久化到 ConfigStore（分号分隔字符串）。
/// </summary>
public sealed class CookieStore
{
    private readonly ConfigStore _config;
    private readonly Dictionary<string, Dictionary<string, string>> _store = new();

    public CookieStore(ConfigStore config)
    {
        _config = config;
        // 从配置恢复
        var saved = config.Cookies;
        if (!string.IsNullOrWhiteSpace(saved))
        {
            foreach (var segment in saved.Split(';'))
            {
                var trimmed = segment.Trim();
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var name = trimmed[..eq].Trim();
                var value = trimmed[(eq + 1)..].Trim();
                if (name.Length == 0 || value.Length == 0) continue;
                Set("bilibili.com", name, value);
            }
        }
    }

    public void Set(string domain, string name, string value)
    {
        if (!_store.TryGetValue(domain, out var map))
        {
            map = new Dictionary<string, string>();
            _store[domain] = map;
        }
        map[name] = value;
        Persist();
    }

    public void SaveFromResponse(string urlHost, IEnumerable<(string Name, string Value, string Domain)> cookies)
    {
        foreach (var (name, value, domain) in cookies)
        {
            var d = string.IsNullOrWhiteSpace(domain) ? urlHost : domain;
            Set(d, name, value);
        }
    }

    /// <summary>获取对某 host 生效的 Cookie 头（精确匹配 + 父域匹配）</summary>
    public string CookieHeaderFor(string host)
    {
        var result = new Dictionary<string, string>();
        foreach (var (domain, map) in _store)
        {
            var clean = domain.StartsWith('.') ? domain : "." + domain;
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(clean, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (k, v) in map) result[k] = v;
            }
        }
        return string.Join("; ", result.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <summary>获取完整 Cookie 字符串（用于持久化）</summary>
    public string CookieString()
    {
        var all = new List<string>();
        foreach (var map in _store.Values)
            foreach (var (k, v) in map)
                all.Add($"{k}={v}");
        return string.Join("; ", all);
    }

    public void Clear()
    {
        _store.Clear();
        _config.ClearCookies();
    }

    private void Persist()
    {
        var str = CookieString();
        if (!string.IsNullOrWhiteSpace(str)) _config.Cookies = str;
    }
}
